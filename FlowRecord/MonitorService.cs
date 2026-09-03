using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlowRecord.Monitor;

public class MonitorService {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private string currentWindow = "";
    private DateTime windowStartTime = DateTime.Now;
    private string? connectionString;
    private CancellationTokenSource? _cts;
    private long? _bootShutdownId;
    private long? _currentWindowRecordId;
    private long? _sleepWakeId;
    private bool _shutdownRecorded;
    private readonly SemaphoreSlim _shutdownLock = new(1, 1);

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

    public void Initialize() {
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
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS boot_shutdown (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    boot_time TEXT NOT NULL,
    shutdown_time TEXT NULL,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS sleep_wake (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sleep_time TEXT NULL,
    wake_time TEXT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_active_window_start_time ON active_window (start_time DESC);
CREATE INDEX IF NOT EXISTS idx_boot_shutdown_boot_time ON boot_shutdown (boot_time DESC);";

        using var cmd = new SqliteCommand(schema, conn);
        cmd.ExecuteNonQuery();
    }

    // ★起動時の処理をここでまとめて実行する
    public void Start() {
        _cts = new CancellationTokenSource();

        Task.Run(async () => {
            // 1) 起動時間をDBに保存してIDを確保
            // 2) 監視ループ開始
            try {
                _bootShutdownId = await CreateBootRecordAsync(DateTime.Now);

                await MonitoringLoop(_cts.Token);
            } catch (Exception ex) {
                Debug.WriteLine($"MonitorService.Start error: {ex}");
            }
        });
    }

    private async Task MonitoringLoop(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                string activeWindow = GetActiveWindowTitle();
                if (activeWindow != currentWindow) {
                    if (!string.IsNullOrEmpty(currentWindow)) {
                        await CloseCurrentWindowAsync(DateTime.Now);
                        currentWindow = "";
                        _currentWindowRecordId = null;
                    }
                    if (!string.IsNullOrEmpty(activeWindow)) {
                        currentWindow = activeWindow;
                        windowStartTime = DateTime.Now;
                        _currentWindowRecordId = await CreateActiveWindowStartAsync(currentWindow, windowStartTime);
                    }
                }
                await Task.Delay(1000, token);
            } catch (TaskCanceledException) { break; } catch (Exception ex) { Debug.WriteLine($"Error: {ex.Message}"); }
        }
    }

    private static string GetActiveWindowTitle() {
        IntPtr handle = GetForegroundWindow();
        var className = new StringBuilder(256);
        _ = GetClassName(handle, className, 256);
        if (className.ToString() is "Progman" or "WorkerW") return "";
        StringBuilder text = new(256);
        if (GetWindowText(handle, text, 256) > 0) {
            _ = GetWindowThreadProcessId(handle, out var processId);
            try {
                Process process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            } catch { return text.ToString(); }
        }
        return "";
    }

    // スリープ時：DBに sleep_time だけの行を直接書き込む（同期）。
    // ローカルのSQLiteファイルへの書き込みは高速なため、中断前に完了させられる
    // Modern Standbyでは短時間のsuspend/resumeが連続することがあるため、
    // 未確定の行が既にある場合は新規行を作らず、最初のスリープ時刻を保持する
    public void RecordSleep(DateTime sleepTime) {
        if (_sleepWakeId.HasValue) return;
        if (string.IsNullOrWhiteSpace(connectionString)) return;
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

    private CancellationTokenSource? _wakeConfirmCts;
    private static readonly TimeSpan WakeConfirmDelay = TimeSpan.FromSeconds(5);

    // 直前の復帰確定待ちをキャンセルする（再度スリープした＝一時的な復帰だった場合に呼ぶ）
    public void CancelPendingWake() {
        _wakeConfirmCts?.Cancel();
        _wakeConfirmCts = null;
    }

    // 復帰イベントを即確定せず、一定時間後も再スリープしていなければ本復帰とみなしてDBへ書き込む
    public void ScheduleWakeConfirmation(DateTime wakeTime) {
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

    // 復帰確定時：スリープ時に作成した行の wake_time を直接更新する
    private async Task RecordWakeAsync(DateTime wakeTime) {
        if (!_sleepWakeId.HasValue || string.IsNullOrWhiteSpace(connectionString)) return;
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
        } catch (Exception ex) {
            Debug.WriteLine($"RecordWakeAsync error: {ex.Message}");
        } finally {
            _sleepWakeId = null;
        }
    }

    // Exitボタン用：DBへ shutdown_time を書く
    public async Task RecordShutdownAndStopAsync(DateTime shutdownTime) {
        _cts?.Cancel();
        await FlushCurrentWindowAsync(shutdownTime);
        await RecordShutdownAsync(shutdownTime);
    }

    // OS シャットダウン/ログオフ通知用：DBへ shutdown_time を直接書く（同期）。
    // SessionEnding はプロセスが強制終了されるまでの猶予が短いため、
    // ローカルSQLiteへの同期書き込みで完結させる
    public void RecordShutdownSync(DateTime shutdownTime) {
        if (_shutdownRecorded) return;
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        try {
            _bootShutdownId ??= GetLatestBootRecordIdSync();
            if (!_bootShutdownId.HasValue) return;

            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            const string query = @"
UPDATE boot_shutdown
SET shutdown_time = @shutdown_time
WHERE id = @id AND shutdown_time IS NULL";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@shutdown_time", shutdownTime);
            cmd.Parameters.AddWithValue("@id", _bootShutdownId.Value);
            cmd.ExecuteNonQuery();

            _shutdownRecorded = true;
        } catch (Exception ex) {
            Debug.WriteLine($"RecordShutdownSync error: {ex.Message}");
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
            _ = await cmd.ExecuteNonQueryAsync();

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
            _ = await cmd.ExecuteNonQueryAsync();
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
SELECT
    'active_window' AS event_type,
    aw.window_title,
    aw.start_time,
    aw.end_time
FROM active_window aw
UNION ALL
SELECT
    'startup' AS event_type,
    'system' AS window_title,
    bs.boot_time AS start_time,
    NULL AS end_time
FROM boot_shutdown bs
UNION ALL
SELECT
    'shutdown' AS event_type,
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

            var query = $@"
WITH ds(date) AS (VALUES {dateParamNames})
SELECT
    ds.date AS date,
    COALESCE(SUM(
        CASE WHEN bs.boot_time IS NOT NULL THEN
            (MIN(julianday(COALESCE(bs.shutdown_time, @now)), julianday(datetime(ds.date, '+1 day')))
             - MAX(julianday(bs.boot_time), julianday(ds.date))) * 24.0
        END
    ), 0) AS total_hours
FROM ds
LEFT JOIN boot_shutdown bs ON date(bs.boot_time) = ds.date
GROUP BY ds.date
ORDER BY ds.date ASC";

            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@now", DateTime.Now);
            for (int i = 0; i < dates.Length; i++) {
                cmd.Parameters.AddWithValue($"@d{i}", dates[i].ToString("yyyy-MM-dd"));
            }
            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await reader.ReadAsync()) {
                results.Add(new {
                    date = reader["date"].ToString(),
                    total_hours = Math.Round(Convert.ToDouble(reader["total_hours"]), 2)
                });
            }
            return JsonSerializer.Serialize(new { type = "bootDurations", data = results });
        } catch (Exception ex) {
            Debug.WriteLine($"GetDailyBootDurationJsonAsync error: {ex.Message}");
            return "{\"type\":\"bootDurations\",\"data\":[]}";
        }
    }

    public async Task<string> GetActiveWindowDurationJsonAsync() {
        try {
            if (string.IsNullOrWhiteSpace(connectionString)) return "{\"type\":\"activeWindowDurations\",\"data\":[]}";
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            // 今日の0時から明日（今日+1日）の0時までの範囲で計算
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);
            var now = DateTime.Now;

            const string query = @"
SELECT
    window_title,
    SUM((julianday(MIN(COALESCE(end_time, @now), @tomorrow)) - julianday(start_time)) * 24.0) AS duration_hours
FROM active_window
WHERE start_time >= @today
  AND start_time < @tomorrow
GROUP BY window_title
HAVING SUM((julianday(MIN(COALESCE(end_time, @now), @tomorrow)) - julianday(start_time)) * 24.0) > 0
ORDER BY duration_hours DESC";

            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@today", today);
            cmd.Parameters.AddWithValue("@tomorrow", tomorrow);

            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await reader.ReadAsync()) {
                results.Add(new {
                    window_title = reader["window_title"].ToString(),
                    duration_hours = Math.Round(Convert.ToDouble(reader["duration_hours"]), 4)
                });
            }
            return JsonSerializer.Serialize(new { type = "activeWindowDurations", data = results });
        } catch (Exception ex) {
            Debug.WriteLine($"GetActiveWindowDurationJsonAsync error: {ex.Message}");
            return "{\"type\":\"activeWindowDurations\",\"data\":[]}";
        }
    }
}
