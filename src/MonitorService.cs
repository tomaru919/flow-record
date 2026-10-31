using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
    private string? connectionString;
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

    private static string AppDataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowRecord"
        );

#if DEBUG
    private static string DbPath => Path.Combine(AppDataDir, "flowrecord.debug.db");
#else
    private static string DbPath => Path.Combine(AppDataDir, "flowrecord.db");
#endif

    public MonitorService() {
        Initialize();
        Start();
    }

    private void Initialize() {
        Directory.CreateDirectory(AppDataDir);

        connectionString = $"Data Source={DbPath}";

        InitializeDatabase();
    }

    private void InitializeDatabase() {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        const string schema = @"
CREATE TABLE IF NOT EXISTS active_window (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    window_title TEXT NOT NULL,
    start_time TEXT NOT NULL,
    end_time TEXT NULL,
    created_at TEXT NOT NULL,
    last_seen TEXT NULL
);

CREATE TABLE IF NOT EXISTS boot_shutdown (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    boot_time TEXT NOT NULL,
    shutdown_time TEXT NULL,
    created_at TEXT NOT NULL,
    last_seen TEXT NULL
);

CREATE TABLE IF NOT EXISTS sleep_wake (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sleep_time TEXT NULL,
    wake_time TEXT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_active_window_start_time ON active_window (start_time DESC);
CREATE INDEX IF NOT EXISTS idx_boot_shutdown_boot_time ON boot_shutdown (boot_time DESC);

-- active_window.window_title にはプロセス名（例: Code）を記録し、表示用の名前（例: Visual Studio Code）はここで対応付ける
CREATE TABLE IF NOT EXISTS app_name (
    process_name TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    icon BLOB NULL
);";

        using var cmd = new SqliteCommand(schema, conn);
        cmd.ExecuteNonQuery();

        // CREATE TABLE IF NOT EXISTS はテーブルが既にあると何もしないので、後から増やした列は既存の DB にここで追加する
        AddColumnIfMissing(conn, "app_name", "icon", "BLOB");
        AddColumnIfMissing(conn, "boot_shutdown", "last_seen", "TEXT");
        AddColumnIfMissing(conn, "active_window", "last_seen", "TEXT");
    }

    // pragma_table_info でテーブルの列一覧を調べ、指定した列が無ければ ALTER TABLE で追加する。
    // テーブル名・列名はパラメーターにできない（値にしか使えない）ので文字列に埋め込む。コード内の固定文字列だけを渡すこと
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition) {
        using var check = new SqliteCommand("SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column", conn);
        check.Parameters.AddWithValue("@table", table);
        check.Parameters.AddWithValue("@column", column);
        if ((long)check.ExecuteScalar()! == 1) return;

        using var alter = new SqliteCommand($"ALTER TABLE {table} ADD COLUMN {column} {definition}", conn);
        alter.ExecuteNonQuery();
    }

    private void Start() {
        // 新しい起動記録やウィンドウの行を作る前に呼ぶ（この時点で終了時刻が NULL の行は、すべて前回までの孤立行）
        RepairOrphanRecords();

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

    // 強制終了・停電で終了時刻が NULL のまま残った行を、最後に動いていた時刻（last_seen）で閉じる。
    // 閉じないと集計で孤立行として開始時刻にフォールバックし、そのセッションやウィンドウが 0 時間になってしまう。
    // last_seen が NULL の行（last_seen 列を追加する前の行など）は終わった時刻が分からないので、そのまま残す。
    // sleep_wake には last_seen がない（スリープ中は何も書けない）ので、復帰時刻はウィンドウの記録から推定して埋める。
    // 埋めないと復帰直後にシャットダウンした場合などにスリープが 0 時間になり、寝ていた時間が使用時間に数えられてしまう
    private void RepairOrphanRecords() {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        try {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            const string query = @"
UPDATE boot_shutdown SET shutdown_time = last_seen WHERE shutdown_time IS NULL AND last_seen IS NOT NULL;
UPDATE active_window SET end_time = last_seen WHERE end_time IS NULL AND last_seen IS NOT NULL;

-- 終了時刻が NULL のスリープ行は、復帰時刻を書く前に終わった孤立行。
-- 同じ起動セッションの中で、スリープ後に最初に始まったウィンドウの開始時刻を復帰時刻とみなす。
-- ウィンドウがなければ、セッションがスリープ後まで続いていたらその終了時刻（ロック画面のままシャットダウンなど）、
-- 続いていなければスリープ時刻（スリープ中の電源断）にする。
-- セッションの終了時刻を使うので、上の boot_shutdown の修復より後に実行する
UPDATE sleep_wake
SET wake_time = COALESCE(
        -- 復帰すると監視ループがすぐにウィンドウの記録を始めるので、最初のウィンドウの開始時刻が復帰時刻に近い。
        -- 次の起動のウィンドウを拾わないよう、session_end までに始まったものに限る
        (
            SELECT aw.start_time
            FROM active_window aw
            WHERE julianday(aw.start_time) >= julianday(sleep_wake.sleep_time)
                AND julianday(aw.start_time) <= julianday(s.session_end)
            ORDER BY julianday(aw.start_time)
            LIMIT 1
        ), CASE
            WHEN julianday(s.session_end) > julianday(sleep_wake.sleep_time) THEN s.session_end
            ELSE sleep_wake.sleep_time
        END
    )
-- 直す行（wake_time が NULL）ごとに、そのスリープを含む起動セッション（スリープ直前に起動したもの）の終了時刻を求める。
-- SET の 2 か所で使うので、ここで 1 回だけ計算して s.session_end として使い回す
FROM (
        SELECT sw.id,
            (
                SELECT bs.shutdown_time
                FROM boot_shutdown bs
                WHERE julianday(bs.boot_time) <= julianday(sw.sleep_time)
                ORDER BY julianday(bs.boot_time) DESC
                LIMIT 1
            ) AS session_end
        FROM sleep_wake sw
        WHERE sw.wake_time IS NULL
            AND sw.sleep_time IS NOT NULL
    ) AS s
WHERE sleep_wake.id = s.id;";

            using var cmd = new SqliteCommand(query, conn);
            cmd.ExecuteNonQuery();
        } catch (Exception ex) {
            // 修復に失敗しても記録は始められるので、起動は止めない
            Debug.WriteLine($"RepairOrphanRecords error: {ex}");
        }
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
        if (string.IsNullOrWhiteSpace(connectionString)) return;
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
        if (appInfo == null || string.IsNullOrWhiteSpace(connectionString)) return;
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
        if (string.IsNullOrWhiteSpace(connectionString)) {
            PowerLogger.Log("RecordSleep skipped: connectionString is empty");
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
        if (string.IsNullOrWhiteSpace(connectionString)) return;
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
        if (!_sleepWakeId.HasValue || string.IsNullOrWhiteSpace(connectionString)) {
            PowerLogger.Log("RecordWakeAsync skipped: no pending sleep row or empty connectionString");
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
        if (string.IsNullOrWhiteSpace(connectionString)) return;
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
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
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
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

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
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
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
            if (string.IsNullOrWhiteSpace(connectionString)) return;

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
            if (string.IsNullOrWhiteSpace(connectionString)) return;
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
            if (string.IsNullOrWhiteSpace(connectionString)) return null;
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
            if (string.IsNullOrWhiteSpace(connectionString)) return;
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

    public async Task<string> GetRecordsJsonAsync() {
        try {
            if (string.IsNullOrWhiteSpace(connectionString)) return "[]";
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
SELECT 'active_window' AS event_type,
    COALESCE(n.display_name, aw.window_title) AS window_title,
    aw.start_time,
    aw.end_time
FROM active_window aw
    LEFT JOIN app_name n ON n.process_name = aw.window_title
UNION ALL
SELECT 'startup' AS event_type,
    'system' AS window_title,
    bs.boot_time AS start_time,
    NULL AS end_time
FROM boot_shutdown bs
UNION ALL
SELECT 'shutdown' AS event_type,
    'system' AS window_title,
    bs.shutdown_time AS start_time,
    NULL AS end_time
FROM boot_shutdown bs
WHERE bs.shutdown_time IS NOT NULL
ORDER BY start_time DESC
LIMIT 100";
            await using var cmd = new SqliteCommand(query, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await reader.ReadAsync()) {
                results.Add(new {
                    window_title = reader["window_title"].ToString(),
                    event_type = reader["event_type"].ToString(),
                    start_time = reader["start_time"].ToString(),
                    end_time = reader["end_time"] == DBNull.Value ? "" : reader["end_time"].ToString()
                });
            }
            return JsonSerializer.Serialize(new { type = "activeWindowRecords", data = results });
        } catch { return "{\"type\":\"activeWindowRecords\",\"data\":[]}"; }
    }

    public async Task<string> GetDailyBootDurationJsonAsync(int weekOffset = 0) {
        try {
            if (string.IsNullOrWhiteSpace(connectionString)) return "{\"type\":\"bootDurations\",\"data\":[]}";
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            // 日曜日始まりの週の開始日を計算し、weekOffset 週分ずらす
            var today = DateTime.Today;
            var currentWeekSunday = today.AddDays(-(int)today.DayOfWeek);
            var weekStart = currentWeekSunday.AddDays(weekOffset * 7);
            var dates = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).ToArray();

            var dateParamNames = string.Join(", ", Enumerable.Range(0, 7).Select(i => $"(@d{i})"));

            // @now まで稼働中とみなしてよいのは _bootShutdownId の現在のセッション（スリープは _sleepWakeId）だけ。
            // 他の shutdown_time/wake_time が NULL の行は孤立（クラッシュ等）のため boot_time/sleep_time にフォールバックする。
            // total_hours（boot_time〜shutdown_time）にはスリープしていた時間も含まれてしまうため、
            // sleep_hours を別途集計し、フロント側で total_hours - sleep_hours を「実際に使用していた時間」として表示する
            var query = $@"
WITH ds(date) AS (
    VALUES {dateParamNames}
)
SELECT ds.date AS date,
    COALESCE(
        (
            SELECT SUM(
                    (
                        MIN(
                            julianday(
                                COALESCE(
                                    bs.shutdown_time,
                                    CASE
                                        WHEN bs.id = @currentBootId THEN @now
                                        ELSE bs.boot_time
                                    END
                                )
                            ),
                            julianday(datetime(ds.date, '+1 day'))
                        ) - MAX(julianday(bs.boot_time), julianday(ds.date))
                    ) * 24.0
                )
            FROM boot_shutdown bs
            WHERE julianday(bs.boot_time) < julianday(datetime(ds.date, '+1 day'))
                AND julianday(
                    COALESCE(
                        bs.shutdown_time,
                        CASE
                            WHEN bs.id = @currentBootId THEN @now
                            ELSE bs.boot_time
                        END
                    )
                ) >= julianday(ds.date)
        ),
        0
    ) AS total_hours,
    COALESCE(
        (
            SELECT SUM(
                    (
                        MIN(
                            julianday(
                                COALESCE(
                                    sw.wake_time,
                                    CASE
                                        WHEN sw.id = @currentSleepId THEN @now
                                        ELSE sw.sleep_time
                                    END
                                )
                            ),
                            julianday(datetime(ds.date, '+1 day'))
                        ) - MAX(julianday(sw.sleep_time), julianday(ds.date))
                    ) * 24.0
                )
            FROM sleep_wake sw
            WHERE sw.sleep_time IS NOT NULL
                AND julianday(sw.sleep_time) < julianday(datetime(ds.date, '+1 day'))
                AND julianday(
                    COALESCE(
                        sw.wake_time,
                        CASE
                            WHEN sw.id = @currentSleepId THEN @now
                            ELSE sw.sleep_time
                        END
                    )
                ) >= julianday(ds.date)
        ),
        0
    ) AS sleep_hours
FROM ds
ORDER BY ds.date ASC";

            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@now", DateTime.Now);
            cmd.Parameters.AddWithValue("@currentBootId", (object?)_bootShutdownId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@currentSleepId", (object?)_sleepWakeId ?? DBNull.Value);
            for (int i = 0; i < dates.Length; i++) {
                cmd.Parameters.AddWithValue($"@d{i}", dates[i].ToString("yyyy-MM-dd"));
            }
            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await reader.ReadAsync()) {
                var totalHours = Convert.ToDouble(reader["total_hours"]);
                var sleepHours = Math.Min(Convert.ToDouble(reader["sleep_hours"]), totalHours);
                results.Add(new {
                    date = reader["date"].ToString(),
                    total_hours = Math.Round(totalHours, 2),
                    sleep_hours = Math.Round(sleepHours, 2)
                });
            }
            return JsonSerializer.Serialize(new { type = "bootDurations", data = results });
        } catch (Exception ex) {
            Debug.WriteLine($"GetDailyBootDurationJsonAsync error: {ex.Message}");
            return "{\"type\":\"bootDurations\",\"data\":[]}";
        }
    }

    // dayOffset: 0 が今日、-1 が昨日
    public async Task<string> GetActiveWindowDurationJsonAsync(int dayOffset = 0) {
        try {
            if (string.IsNullOrWhiteSpace(connectionString)) return "{\"type\":\"activeWindowDurations\",\"data\":[]}";
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            // 対象日の0時から翌日の0時までの範囲で計算
            var dayStart = DateTime.Today.AddDays(dayOffset);
            var dayEnd = dayStart.AddDays(1);
            var now = DateTime.Now;

            // @now まで表示中とみなしてよいのは _currentWindowRecordId の行だけ。他の end_time NULL 行は孤立（強制終了等）のため start_time にフォールバックする。
            // 表示名（app_name）があればそれで集計し、なければ記録したプロセス名のまま表示する
            const string query = @"
SELECT COALESCE(n.display_name, aw.window_title) AS window_title,
    SUM(
        (
            julianday(
                MIN(
                    COALESCE(
                        aw.end_time,
                        CASE
                            WHEN aw.id = @currentWindowRecordId THEN @now
                            ELSE aw.start_time
                        END
                    ),
                    @dayEnd
                )
            ) - julianday(aw.start_time)
        ) * 24.0
    ) AS duration_hours,
    MAX(n.icon) AS icon
FROM active_window aw
    LEFT JOIN app_name n ON n.process_name = aw.window_title
WHERE aw.start_time >= @dayStart
    AND aw.start_time < @dayEnd
GROUP BY COALESCE(n.display_name, aw.window_title)
HAVING duration_hours > 0
ORDER BY duration_hours DESC";

            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@dayStart", dayStart);
            cmd.Parameters.AddWithValue("@dayEnd", dayEnd);
            cmd.Parameters.AddWithValue("@currentWindowRecordId", (object?)_currentWindowRecordId ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await reader.ReadAsync()) {
                results.Add(new {
                    window_title = reader["window_title"].ToString(),
                    duration_hours = Math.Round(Convert.ToDouble(reader["duration_hours"]), 4),
                    icon = reader["icon"] is byte[] bytes
                        ? "data:image/png;base64," + Convert.ToBase64String(bytes)
                        : null
                });
            }
            return JsonSerializer.Serialize(new { type = "activeWindowDurations", data = results });
        } catch (Exception ex) {
            Debug.WriteLine($"GetActiveWindowDurationJsonAsync error: {ex.Message}");
            return "{\"type\":\"activeWindowDurations\",\"data\":[]}";
        }
    }
}
