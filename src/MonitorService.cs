using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using FlowRecord.Logging;
using System.Drawing.Imaging;

namespace FlowRecord.Monitor;

public partial class MonitorService {
    [LibraryImport("user32.dll")] private static partial IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO {
        public uint cbSize;
        public uint dwTime;
    }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [LibraryImport("kernel32.dll")] private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);
    // char を UTF-16（Windows の W 版 API の文字形式）として渡すことを明示する。LibraryImport は文字の形式を暗黙に決めない
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    // 管理者権限で動くアプリ（タスクマネージャーなど）でも exe のパスだけは読める最小限の権限
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private string currentWindow = "";
    private DateTime windowStartTime = DateTime.Now;
    private readonly string connectionString;
    private CancellationTokenSource? _cts;
    private long? _bootShutdownId;
    private long? _currentWindowRecordId;
    private long? _sleepWakeId;
    private bool _shutdownRecorded;
    private readonly SemaphoreSlim _shutdownLock = new(1, 1);
    // 監視ループ（バックグラウンド）とスリープ通知（UIスレッド）の両方が currentWindow/_currentWindowRecordId を書き換えるため排他する
    private readonly SemaphoreSlim _windowLock = new(1, 1);
    private volatile bool _isSuspended;
    // このセッションで表示名を保存済みのプロセス名。ウィンドウが切り替わるたびに DB へ書かないようにする
    private readonly HashSet<string> _savedAppInfos = [];
    // 強制終了・停電に備えて「最後に動いていた時刻」（last_seen）を DB に書く間隔。落ちたときに失う時間は最大でこの長さになる
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);
    // 最後に last_seen を書いた時刻。MinValue にしておくと、起動直後とスリープ復帰直後の最初のループですぐに書く
    private DateTime _lastHeartbeat = DateTime.MinValue;
    // キーボード・マウスの操作がこの時間ないと離席中とみなす。短すぎると動画を見ているだけの時間まで離席扱いになる
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);
    // 離席中かどうか。離席に入った最初のループだけ記録を閉じ、以降のループで何度も閉じないようにする
    private bool _isIdle;
    // 画面ロック中かどうか。_isSuspended と分けるのは、ロック中にスリープして復帰すると
    // ResumeMonitoring が _isSuspended を戻してしまい、ロック画面が使用時間として記録されるため
    private volatile bool _isLocked;

    public MonitorService() {
        // スキーマの作成と孤立行の修復をしてから、記録を始める
        var database = new Database();
        database.Initialize();
        connectionString = database.ConnectionString;
        _usageQueries = new UsageQueries(connectionString);
        Start();
    }

    // 画面用の集計。UsageQueries は状態を持たないので、今進行中の行の ID と現在時刻をここで渡す
    private readonly UsageQueries _usageQueries;
    private CurrentRecordIds CurrentIds => new(_bootShutdownId, _currentWindowRecordId, _sleepWakeId);

    public Task<List<ActivityRecord>> GetRecordsAsync() =>
        _usageQueries.GetRecordsAsync();

    public Task<List<BootDuration>> GetDailyBootDurationsAsync(int weekOffset) =>
        _usageQueries.GetDailyBootDurationsAsync(weekOffset, CurrentIds, DateTime.Now);

    public Task<List<HourlyUsage>> GetHourlyUsageAsync(DateTime day) =>
        _usageQueries.GetHourlyUsageAsync(day, CurrentIds, DateTime.Now);

    public Task<List<ActiveWindowDuration>> GetActiveWindowDurationsAsync(int dayOffset) =>
        _usageQueries.GetActiveWindowDurationsAsync(dayOffset, CurrentIds, DateTime.Now);

    private void Start() {
        _cts = new CancellationTokenSource();

        Task.Run(async () => {
            try {
                _bootShutdownId = await CreateBootRecordAsync(DateTime.Now);

                await MonitoringLoop(_cts.Token);
            } catch (Exception ex) {
                Debug.WriteLine($"MonitorService.Start error: {ex}");
            }
        });
    }

    // 最後にキーボード・マウスの操作があってからの経過時間を返す
    private static TimeSpan GetIdleTime() {
        // cbSize を設定しないと GetLastInputInfo は失敗する（構造体の版を判別するため）
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        // 取得できないときは「操作中」として扱い、記録を止めないようにする
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // dwTime も TickCount も起動からのミリ秒で、約49.7日で 0 に戻る。
        // uint のまま引き算すると、0 に戻った前後でも桁あふれによって正しい差になる
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    private async Task MonitoringLoop(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                await _windowLock.WaitAsync(token);
                try {
                    // スリープ中は記録しない（Modern Standbyではプロセスが完全には凍結されないことがある）。
                    // 画面ロック中もパソコンを使っていないので記録しない
                    if (!_isSuspended && !_isLocked) {
                        // ループは 1 秒ごとに回るが、DB への書き込みを減らすため last_seen は HeartbeatInterval ごとに書く。
                        // スリープ中は書かない（書くとスリープしていた時間まで使用時間に数えられてしまう）
                        var now = DateTime.Now;
                        if (now - _lastHeartbeat >= HeartbeatInterval) {
                            await UpdateLastSeenAsync(now);
                            _lastHeartbeat = now;
                        }

                        var idle = GetIdleTime();
                        // 離席中はウィンドウの記録を閉じ、使用時間に数えないようにする
                        if (idle >= IdleThreshold) {
                            if (!_isIdle) {
                                _isIdle = true;
                                // 離席に気づくのは操作が止まってから IdleThreshold 後なので、最後に操作した時刻までさかのぼって閉じる
                                var idleStart = now - idle;
                                // 操作がないままウィンドウが切り替わっていると（通知やほかのアプリが前面に出たときなど）、
                                // 最後の操作は今のウィンドウの開始より前になる。そのまま閉じると終了時刻が開始時刻より前になり、
                                // 使用時間がマイナスになるので、開始時刻（0分）でそろえる
                                if (idleStart < windowStartTime) idleStart = windowStartTime;
                                Debug.WriteLine($"Idle detected. | idleStart: {idleStart}, _currentWindowRecordId: {_currentWindowRecordId}");
                                if (!string.IsNullOrEmpty(currentWindow)) {
                                    // 先に currentWindow を空にすると、最初の INSERT に失敗していたときの予備の保存ができなくなるので、閉じてから空にする
                                    await CloseCurrentWindowAsync(idleStart);
                                    currentWindow = "";
                                }
                            }
                        } else {
                            // 操作が再開したら currentWindow は空なので、下の処理で表示中ウィンドウの記録を新しく始める
                            _isIdle = false;
                            string activeWindow = GetActiveWindowTitle(out var processId);
                            if (activeWindow != currentWindow) {
                                if (!string.IsNullOrEmpty(currentWindow)) {
                                    await CloseCurrentWindowAsync(DateTime.Now);
                                    currentWindow = "";
                                    _currentWindowRecordId = null;
                                }
                                if (!string.IsNullOrEmpty(activeWindow)) {
                                    if (processId != 0 && !_savedAppInfos.Contains(activeWindow)) {
                                        await SaveAppInfo(activeWindow, processId);
                                    }
                                    currentWindow = activeWindow;
                                    windowStartTime = DateTime.Now;
                                    _currentWindowRecordId = await CreateActiveWindowStartAsync(currentWindow, windowStartTime);
                                }
                            }
                        }
                    }
                } finally {
                    _windowLock.Release();
                }
                await Task.Delay(1000, token);
            } catch (TaskCanceledException) { break; } catch (Exception ex) { Debug.WriteLine($"Error: {ex.Message}"); }
        }
    }

    // 現在のセッション（boot_shutdown）と表示中ウィンドウ（active_window）の行に「この時刻まで動いていた」と書く。
    // 次回起動時に RepairOrphanRecords がこの時刻を終了時刻として使う。
    // ID が null（デスクトップ表示中などで記録中の行がない）のときは DBNull を渡すので、どの行にも一致せず何もしない
    private async Task UpdateLastSeenAsync(DateTime now) {
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
UPDATE boot_shutdown SET last_seen = @now WHERE id = @bootShutdownId;
UPDATE active_window SET last_seen = @now WHERE id = @currentWindowRecordId;";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@bootShutdownId", (object?)_bootShutdownId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@currentWindowRecordId", (object?)_currentWindowRecordId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        } catch (Exception ex) {
            // 失敗しても次の HeartbeatInterval でまた書くので、監視は止めない
            Debug.WriteLine($"UpdateLastSeenAsync error: {ex.Message}");
        }
    }

    // processId はプロセス名を取得できたときだけ設定する（取得できずウィンドウのタイトルを返したときは 0）
    private static string GetActiveWindowTitle(out uint processId) {
        processId = 0;
        IntPtr handle = GetForegroundWindow();
        var className = new StringBuilder(256);
        _ = GetClassName(handle, className, 256);
        if (className.ToString() is "Progman" or "WorkerW") return "";
        StringBuilder text = new(256);
        if (GetWindowText(handle, text, 256) > 0) {
            _ = GetWindowThreadProcessId(handle, out var pid);
            try {
                Process process = Process.GetProcessById((int)pid);
                processId = pid;
                return process.ProcessName;
            } catch { return text.ToString(); }
        }
        return "";
    }

    private sealed record AppInfo(string? DisplayName, byte[]? IconPng);

    private static AppInfo? GetAppInfo(uint processId) {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero) return null;
        try {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            if (!QueryFullProcessImageName(hProcess, 0, buffer, ref size)) return null;
            var path = new string(buffer, 0, (int)size);

            var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return new AppInfo(
                string.IsNullOrEmpty(description) ? null : description,
                GetIconPng(path));
        } catch {
            return null;
        } finally {
            CloseHandle(hProcess);
        }
    }

    private static byte[]? GetIconPng(string path) {
        using var icon = Icon.ExtractAssociatedIcon(path);
        if (icon is null) return null;
        using var bmp = icon.ToBitmap();
        using var stream = new MemoryStream();
        bmp.Save(stream, ImageFormat.Png);
        return stream.ToArray();   // ここでコピーを取り出すので、stream が破棄されても配列は残る
    }

    private async Task SaveAppInfo(string processName, uint processId) {
        var appInfo = GetAppInfo(processId);
        if (appInfo == null) return;
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            // アプリの更新で説明が変わることもあるので、既にある行も上書きする
            const string query = @"
INSERT INTO app_name (process_name, display_name, icon)
VALUES (@process_name, @display_name, @icon)
ON CONFLICT (process_name) DO UPDATE SET
    display_name = excluded.display_name,
    icon = COALESCE(excluded.icon, app_name.icon)";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@process_name", processName);
            cmd.Parameters.AddWithValue("@display_name", appInfo.DisplayName ?? processName);
            cmd.Parameters.AddWithValue("@icon", (object?)appInfo.IconPng ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
            _savedAppInfos.Add(processName);
        } catch (Exception ex) {
            Debug.WriteLine($"SaveAppInfoAsync error: {ex.Message}");
        }
    }

    // Modern Standbyの短時間suspend/resumeに対応：未確定行があれば最初のスリープ時刻を保持する
    public void RecordSleep(DateTime sleepTime) {
        PowerLogger.Log($"RecordSleep called: {sleepTime}");
        if (_sleepWakeId.HasValue) {
            PowerLogger.Log("RecordSleep skipped: unconfirmed sleep already pending");
            return;
        }
        try {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            const string query = @"
INSERT INTO sleep_wake (sleep_time, wake_time, created_at)
VALUES (@sleep_time, NULL, @created_at);
SELECT last_insert_rowid();";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@sleep_time", sleepTime);
            cmd.Parameters.AddWithValue("@created_at", DateTime.Now);
            var result = cmd.ExecuteScalar();
            if (result != null && result != DBNull.Value) _sleepWakeId = Convert.ToInt64(result);
            Debug.WriteLine($"Sleep recorded: {sleepTime}");
        } catch (Exception ex) {
            Debug.WriteLine($"RecordSleep error: {ex.Message}");
        }
    }

    // スリープ直前に表示中ウィンドウの記録を閉じ、復帰まで監視を止める。
    // 閉じずにおくと、同じウィンドウのまま復帰した場合にスリープ時間がそのウィンドウの使用時間に含まれてしまう
    public void SuspendMonitoring(DateTime sleepTime) {
        _windowLock.Wait();
        try {
            _isSuspended = true;
            if (_currentWindowRecordId.HasValue) {
                CloseActiveWindowSync(_currentWindowRecordId.Value, sleepTime);
            }
            currentWindow = "";
            _currentWindowRecordId = null;
        } finally {
            _windowLock.Release();
        }
    }

    // 復帰直後から監視を再開し、次のループで表示中ウィンドウの記録を新しく開始する
    public void ResumeMonitoring() {
        _isSuspended = false;
    }

    private void CloseActiveWindowSync(long recordId, DateTime endTime) {
        try {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            const string query = @"
UPDATE active_window
SET end_time = @end_time
WHERE id = @id AND end_time IS NULL";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@end_time", endTime);
            cmd.Parameters.AddWithValue("@id", recordId);
            cmd.ExecuteNonQuery();
        } catch (Exception ex) {
            Debug.WriteLine($"CloseActiveWindowSync error: {ex.Message}");
        }
    }

    private CancellationTokenSource? _wakeConfirmCts;
    private static readonly TimeSpan WakeConfirmDelay = TimeSpan.FromSeconds(5);

    // 再スリープ（一時的な復帰）時に呼び、直前の確定待ちを取り消す
    public void CancelPendingWake() {
        _wakeConfirmCts?.Cancel();
        _wakeConfirmCts = null;
    }

    // 復帰イベントを即確定せず、一定時間後も再スリープしていなければ本復帰とみなしてDBへ書き込む
    public void ScheduleWakeConfirmation(DateTime wakeTime) {
        PowerLogger.Log($"ScheduleWakeConfirmation called: {wakeTime}");
        _wakeConfirmCts?.Cancel();
        var cts = new CancellationTokenSource();
        _wakeConfirmCts = cts;
        _ = ConfirmWakeAfterDelayAsync(wakeTime, cts.Token);
    }

    private async Task ConfirmWakeAfterDelayAsync(DateTime wakeTime, CancellationToken token) {
        try {
            await Task.Delay(WakeConfirmDelay, token);
        } catch (TaskCanceledException) {
            return;
        }
        if (token.IsCancellationRequested) return;
        await RecordWakeAsync(wakeTime);
    }

    private async Task RecordWakeAsync(DateTime wakeTime) {
        PowerLogger.Log($"RecordWakeAsync called: {wakeTime}, pendingId={_sleepWakeId}");
        if (!_sleepWakeId.HasValue) {
            PowerLogger.Log("RecordWakeAsync skipped: no pending sleep row");
            return;
        }
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
UPDATE sleep_wake
SET wake_time = @wake_time
WHERE id = @id";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@wake_time", wakeTime);
            cmd.Parameters.AddWithValue("@id", _sleepWakeId.Value);
            await cmd.ExecuteNonQueryAsync();
            Debug.WriteLine($"Wake recorded: id={_sleepWakeId}, wake={wakeTime}");
            PowerLogger.Log($"Wake recorded: id={_sleepWakeId}, wake={wakeTime}");
        } catch (Exception ex) {
            Debug.WriteLine($"RecordWakeAsync error: {ex.Message}");
            PowerLogger.Log($"RecordWakeAsync error: {ex}");
        } finally {
            _sleepWakeId = null;
        }
    }

    // 画面ロック時に表示中ウィンドウの記録を閉じ、解除まで監視を止める。
    // SessionSwitch は UI スレッドで呼ばれ、すぐに閉じたいので SuspendMonitoring と同じく同期で書く
    public void LockMonitoring(DateTime lockTime) {
        _windowLock.Wait();
        try {
            _isLocked = true;
            if (_currentWindowRecordId.HasValue) {
                CloseActiveWindowSync(_currentWindowRecordId.Value, lockTime);
            }
            currentWindow = "";
            _currentWindowRecordId = null;
        } finally {
            _windowLock.Release();
        }
    }

    // 解除直後から監視を再開し、次のループで表示中ウィンドウの記録を新しく開始する
    public void UnlockMonitoring() {
        _isLocked = false;
    }

    // Exitボタン用：DBへ shutdown_time を書く
    public async Task RecordShutdownAndStopAsync(DateTime shutdownTime) {
        _cts?.Cancel();
        await FlushCurrentWindowAsync(shutdownTime);
        await RecordShutdownAsync(shutdownTime);
    }

    // SessionEnding は強制終了までの猶予が短いため、同期書き込みで完結させる
    public void RecordShutdownSync(DateTime shutdownTime) {
        if (_shutdownRecorded) return;
        _windowLock.Wait();
        try {
            _isSuspended = true;

            _bootShutdownId ??= GetLatestBootRecordIdSync();
            if (!_bootShutdownId.HasValue) return;

            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            const string query = @"
UPDATE boot_shutdown
SET shutdown_time = @shutdown_time
WHERE id = @bootShutdownId AND shutdown_time IS NULL;

UPDATE active_window
SET end_time = @end_time
WHERE id = @currentWindowRecordId AND end_time IS NULL;";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@shutdown_time", shutdownTime);
            cmd.Parameters.AddWithValue("@bootShutdownId", _bootShutdownId.Value);
            cmd.Parameters.AddWithValue("@end_time", shutdownTime);
            cmd.Parameters.AddWithValue("@currentWindowRecordId", (object?)_currentWindowRecordId ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            currentWindow = "";
            _currentWindowRecordId = null;
            _shutdownRecorded = true;
        } catch (Exception ex) {
            Debug.WriteLine($"RecordShutdownSync error: {ex.Message}");
        } finally {
            _windowLock.Release();
        }
    }

    private long? GetLatestBootRecordIdSync() {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        const string query = @"
SELECT id
FROM boot_shutdown
ORDER BY boot_time DESC
LIMIT 1";
        using var cmd = new SqliteCommand(query, conn);
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private async Task FlushCurrentWindowAsync(DateTime endTime) {
        if (string.IsNullOrWhiteSpace(currentWindow)) return;
        await CloseCurrentWindowAsync(endTime);
        currentWindow = "";
    }

    private async Task CloseCurrentWindowAsync(DateTime endTime) {
        if (_currentWindowRecordId.HasValue) {
            await CloseActiveWindowAsync(_currentWindowRecordId.Value, endTime);
            _currentWindowRecordId = null;
            return;
        }
        if (!string.IsNullOrWhiteSpace(currentWindow)) {
            await SaveActiveWindowRecordAsync(currentWindow, windowStartTime, endTime);
        }
    }

    private async Task<long?> CreateBootRecordAsync(DateTime bootTime) {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        const string query = @"
INSERT INTO boot_shutdown (boot_time, created_at)
VALUES (@boot_time, @created_at);
SELECT last_insert_rowid();";
        await using var cmd = new SqliteCommand(query, conn);
        cmd.Parameters.AddWithValue("@boot_time", bootTime);
        cmd.Parameters.AddWithValue("@created_at", DateTime.Now);
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private async Task<long?> GetLatestBootRecordIdAsync() {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        const string query = @"
SELECT id
FROM boot_shutdown
ORDER BY boot_time DESC
LIMIT 1";
        await using var cmd = new SqliteCommand(query, conn);
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private async Task RecordShutdownAsync(DateTime shutdownTime) {
        if (_shutdownRecorded) return;
        await _shutdownLock.WaitAsync();
        try {
            if (_shutdownRecorded) return;

            _bootShutdownId ??= await GetLatestBootRecordIdAsync();
            if (!_bootShutdownId.HasValue) return;

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            const string query = @"
UPDATE boot_shutdown
SET shutdown_time = @shutdown_time
WHERE id = @id AND shutdown_time IS NULL";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@shutdown_time", shutdownTime);
            cmd.Parameters.AddWithValue("@id", _bootShutdownId.Value);
            await cmd.ExecuteNonQueryAsync();

            _shutdownRecorded = true;
        } catch (Exception ex) {
            Debug.WriteLine($"Shutdown record error: {ex}");
        } finally {
            _shutdownLock.Release();
        }
    }

    private async Task SaveActiveWindowRecordAsync(string windowTitle, DateTime startTime, DateTime endTime) {
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
INSERT INTO active_window (window_title, start_time, end_time, created_at)
VALUES (@window_title, @start_time, @end_time, @created_at)";

            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@window_title", windowTitle ?? "");
            cmd.Parameters.AddWithValue("@start_time", startTime);
            cmd.Parameters.AddWithValue("@end_time", endTime);
            cmd.Parameters.AddWithValue("@created_at", DateTime.Now);
            await cmd.ExecuteNonQueryAsync();
        } catch (Exception ex) { Debug.WriteLine($"SaveActiveWindowRecordAsync DB Error: {ex.Message}"); }
    }

    private async Task<long?> CreateActiveWindowStartAsync(string windowTitle, DateTime startTime) {
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
INSERT INTO active_window (window_title, start_time, end_time, created_at)
VALUES (@window_title, @start_time, NULL, @created_at);
SELECT last_insert_rowid();";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@window_title", windowTitle ?? "");
            cmd.Parameters.AddWithValue("@start_time", startTime);
            cmd.Parameters.AddWithValue("@created_at", DateTime.Now);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
        } catch (Exception ex) {
            Debug.WriteLine($"CreateActiveWindowStartAsync DB Error: {ex.Message}");
            return null;
        }
    }

    private async Task CloseActiveWindowAsync(long recordId, DateTime endTime) {
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
UPDATE active_window
SET end_time = @end_time
WHERE id = @id AND end_time IS NULL";
            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@end_time", endTime);
            cmd.Parameters.AddWithValue("@id", recordId);
            await cmd.ExecuteNonQueryAsync();
        } catch (Exception ex) {
            Debug.WriteLine($"CloseActiveWindowAsync DB Error: {ex.Message}");
        }
    }
}
