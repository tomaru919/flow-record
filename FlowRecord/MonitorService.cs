using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DotNetEnv;
using Npgsql;
using System.Collections.Generic;

namespace FlowRecord.Monitor;

public class MonitorService {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private string currentWindow = "";
    private DateTime windowStartTime = DateTime.Now;
    private string? connectionString;
    private readonly string pcName = Environment.MachineName;
    private CancellationTokenSource? _cts;
    private int? _pcNameId;
    private long? _bootShutdownId; // ← Current_startup_id（起動行のID）
    private long? _currentWindowRecordId;
    private bool _shutdownRecorded;
    private readonly SemaphoreSlim _shutdownLock = new(1, 1); // なぜRecordShutdownAsync関数のときだけロックするのか？

    // pendingファイル（OSシャットダウン時にここへ保存）
    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowRecord",
            "shutdown.txt"
        );

    public void Initialize() {
        var envPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".env"));
        if (File.Exists(envPath)) Env.Load(envPath);

#if DEBUG
        connectionString = $"User Id={Environment.GetEnvironmentVariable("SUPABASE_USER")};" +
                            $"Password={Environment.GetEnvironmentVariable("SUPABASE_PASSWORD")};" +
                            $"Server={Environment.GetEnvironmentVariable("SUPABASE_SERVER")};" +
                            $"Port=5432;" +
                            $"Database={Environment.GetEnvironmentVariable("SUPABASE_DB")};" +
                            "SSL Mode=Require;Trust Server Certificate=true";
#else
        connectionString = $"User Id={Environment.GetEnvironmentVariable("PRODUCTION_USER")};" +
                            $"Password={Environment.GetEnvironmentVariable("PRODUCTION_PASSWORD")};" +
                            $"Server={Environment.GetEnvironmentVariable("PRODUCTION_SERVER")};" +
                            $"Port=5432;" +
                            $"Database={Environment.GetEnvironmentVariable("PRODUCTION_DB")};" +
                            "SSL Mode=Require;Trust Server Certificate=true";
#endif
    }

    // ★起動時の処理をここでまとめて実行する
    public void Start() {
        _cts = new CancellationTokenSource();

        Task.Run(async () => {
            // 1) 起動時間をDBに保存してIDを確保（Current_startup_id）
            // 2) pendingファイルを読んで、前回の shutdown_time を UPDATE
            // 3) 監視ループ開始
            try {
                var pcNameId = await EnsurePcNameIdAsync();
                if (!pcNameId.HasValue) return;

                await ApplyShutdownLogToLastBootRecordAsync();

                _bootShutdownId = await CreateBootRecordAsync(DateTime.Now);

                await MonitoringLoop(_cts.Token);
            } catch (Exception ex) {
                // ここで落ちても監視ループは開始しない（DB前提アプリなので）
                Debug.WriteLine($"MonitorService.Start error: {ex}");
            }
        });
    }

    private async Task MonitoringLoop(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                string activeWindow = GetActiveWindowTitle();
                if (activeWindow != currentWindow && !string.IsNullOrEmpty(activeWindow)) {
                    await CloseCurrentWindowAsync(DateTime.Now);
                    currentWindow = activeWindow;
                    windowStartTime = DateTime.Now;
                    _currentWindowRecordId = await CreateActiveWindowStartAsync(currentWindow, windowStartTime);
                }
                await Task.Delay(1000, token);
            } catch (TaskCanceledException) { break; } catch (Exception ex) { Debug.WriteLine($"Error: {ex.Message}"); }
        }
    }

    private static string GetActiveWindowTitle() {
        IntPtr handle = GetForegroundWindow();
        StringBuilder text = new(256);
        if (GetWindowText(handle, text, 256) > 0) {
            _ = GetWindowThreadProcessId(handle, out uint processId);
            try {
                Process process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            } catch { return text.ToString(); }
        }
        return "";
    }

    // Exitボタン用：DBへ shutdown_time を書く
    public async Task RecordShutdownAndStopAsync(DateTime shutdownTime) {
        _cts?.Cancel();
        await FlushCurrentWindowAsync(shutdownTime);
        await RecordShutdownAsync(shutdownTime);
    }

    private async Task ApplyShutdownLogToLastBootRecordAsync() {
        DateTime? shutdownTime;
        try {
            var readText = File.ReadAllLines(LogPath).Last();
            if (string.IsNullOrWhiteSpace(readText)) return;
            shutdownTime = DateTime.Parse(readText);
        } catch (FormatException ex) {
            Debug.WriteLine($"parse error: {ex}");
            return;
        } catch (Exception ex) {
            Debug.WriteLine($"ApplyShutdownLogToLastBootRecordAsync read error: {ex.Message}");
            return;
        }

        var pcNameId = await EnsurePcNameIdAsync();
        if (!pcNameId.HasValue || string.IsNullOrWhiteSpace(connectionString)) return;

        try {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            const string selectId = @"
SELECT id
FROM boot_shutdown
WHERE pc_name_id = @pc_name_id
ORDER BY boot_time DESC
LIMIT 1";

            long? lastId = null;
            await using (var cmd = new NpgsqlCommand(selectId, conn)) {
                cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value) lastId = Convert.ToInt64(result);
            }
            if (!lastId.HasValue) return;

            const string update = @"
UPDATE boot_shutdown
SET shutdown_time = @shutdown_time
WHERE id = @id AND pc_name_id = @pc_name_id;";

            await using (var cmd = new NpgsqlCommand(update, conn)) {
                cmd.Parameters.AddWithValue("shutdown_time", shutdownTime);
                cmd.Parameters.AddWithValue("id", lastId.Value);
                cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);

                var affected = await cmd.ExecuteNonQueryAsync();

                // 成功したらログを消す（次回また同じ shutdown_time を上書きしないため）
                if (affected > 0) {
                    try { File.Delete(LogPath); } catch { }
                }
            }
        } catch (Exception ex) {
            Debug.WriteLine($"ApplyShutdownLogToLastBootRecordAsync error: {ex.Message}");
        }
    }

    public async Task RecordSleepAsync(DateTime sleepTime) {
        await FlushCurrentWindowAsync(sleepTime);

        var pcNameId = await EnsurePcNameIdAsync();
        if (!pcNameId.HasValue || string.IsNullOrWhiteSpace(connectionString)) return;

        try {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            const string query = @"
INSERT INTO sleep_wake (pc_name_id, sleep_time)
VALUES (@pc_name_id, @sleep_time)";

            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
            cmd.Parameters.AddWithValue("sleep_time", sleepTime);
            await cmd.ExecuteNonQueryAsync();
        } catch (Exception ex) {
            Debug.WriteLine($"RecordSleepAsync error: {ex.Message}");
        }
    }

    public async Task RecordWakeAsync(DateTime wakeTime) {
        await FlushCurrentWindowAsync(wakeTime);

        var pcNameId = await EnsurePcNameIdAsync();
        if (!pcNameId.HasValue || string.IsNullOrWhiteSpace(connectionString)) return;

        try {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            // 最新の sleep_time の行を取得する
            const string selectId = @"
SELECT id
FROM sleep_wake
WHERE pc_name_id = @pc_name_id AND wake_time IS NULL
ORDER BY sleep_time DESC
LIMIT 1";

            long? lastId = null;
            await using (var cmd = new NpgsqlCommand(selectId, conn)) {
                cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value) lastId = Convert.ToInt64(result);
            }

            if (!lastId.HasValue) return;

            const string update = @"
UPDATE sleep_wake
SET wake_time = @wake_time
WHERE id = @id AND pc_name_id = @pc_name_id";

            await using (var cmd = new NpgsqlCommand(update, conn)) {
                cmd.Parameters.AddWithValue("wake_time", wakeTime);
                cmd.Parameters.AddWithValue("id", lastId.Value);
                cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
                await cmd.ExecuteNonQueryAsync();
            }
        } catch (Exception ex) {
            Debug.WriteLine($"RecordWakeAsync error: {ex.Message}");
        }
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

    private async Task<int?> EnsurePcNameIdAsync() {
        if (_pcNameId.HasValue) return _pcNameId;
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        const string selectQuery = "SELECT id FROM pc_name WHERE pc_name = @pc_name";
        await using (var selectCmd = new NpgsqlCommand(selectQuery, conn)) {
            selectCmd.Parameters.AddWithValue("pc_name", pcName);
            var result = await selectCmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value) {
                _pcNameId = Convert.ToInt32(result);
                return _pcNameId;
            }
        }

        // パソコンがデーターベースに登録されていない場合、INSERTしてID取得
        const string insertQuery = @"
INSERT INTO pc_name (pc_name)
VALUES (@pc_name)
ON CONFLICT (pc_name) DO UPDATE SET pc_name = EXCLUDED.pc_name
RETURNING id";
        await using var insertCmd = new NpgsqlCommand(insertQuery, conn);
        insertCmd.Parameters.AddWithValue("pc_name", pcName);
        var inserted = await insertCmd.ExecuteScalarAsync();
        if (inserted != null && inserted != DBNull.Value) {
            _pcNameId = Convert.ToInt32(inserted);
        }
        return _pcNameId;
    }

    private async Task<long?> CreateBootRecordAsync(DateTime bootTime) {
        var pcNameId = await EnsurePcNameIdAsync();
        if (!pcNameId.HasValue || string.IsNullOrWhiteSpace(connectionString)) return null;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        const string query = @"
INSERT INTO boot_shutdown (pc_name_id, boot_time)
VALUES (@pc_name_id, @boot_time)
RETURNING id";
        await using var cmd = new NpgsqlCommand(query, conn);
        cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
        cmd.Parameters.AddWithValue("boot_time", bootTime);
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private async Task<long?> GetLatestBootRecordIdAsync(int pcNameId) {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        const string query = @"
SELECT id
FROM boot_shutdown
WHERE pc_name_id = @pc_name_id
ORDER BY boot_time DESC
LIMIT 1";
        await using var cmd = new NpgsqlCommand(query, conn);
        cmd.Parameters.AddWithValue("pc_name_id", pcNameId);
        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
    }

    private async Task RecordShutdownAsync(DateTime shutdownTime) {
        if (_shutdownRecorded) return;
        await _shutdownLock.WaitAsync();
        try {
            if (_shutdownRecorded) return;
            var pcNameId = await EnsurePcNameIdAsync();
            if (!pcNameId.HasValue || string.IsNullOrWhiteSpace(connectionString)) return;

            _bootShutdownId ??= await GetLatestBootRecordIdAsync(pcNameId.Value);
            if (!_bootShutdownId.HasValue) return;

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            const string query = @"
UPDATE boot_shutdown
SET shutdown_time = @shutdown_time
WHERE id = @id AND shutdown_time IS NULL";
            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("shutdown_time", shutdownTime);
            cmd.Parameters.AddWithValue("id", _bootShutdownId.Value);
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
            var pcNameId = await EnsurePcNameIdAsync();
            if (!pcNameId.HasValue || string.IsNullOrWhiteSpace(connectionString)) return;
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
INSERT INTO active_window (pc_name_id, window_title, start_time, end_time)
VALUES (@pc_name_id, @window_title, @start_time, @end_time)";

            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
            cmd.Parameters.AddWithValue("window_title", windowTitle ?? "");
            cmd.Parameters.AddWithValue("start_time", startTime);
            cmd.Parameters.AddWithValue("end_time", endTime);
            await cmd.ExecuteNonQueryAsync();
        } catch (Exception ex) { Debug.WriteLine($"DB Error: {ex.Message}"); }
    }

    private async Task<long?> CreateActiveWindowStartAsync(string windowTitle, DateTime startTime) {
        try {
            var pcNameId = await EnsurePcNameIdAsync();
            if (!pcNameId.HasValue || string.IsNullOrWhiteSpace(connectionString)) return null;
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
INSERT INTO active_window (pc_name_id, window_title, start_time, end_time)
VALUES (@pc_name_id, @window_title, @start_time, NULL)
RETURNING id";
            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
            cmd.Parameters.AddWithValue("window_title", windowTitle ?? "");
            cmd.Parameters.AddWithValue("start_time", startTime);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
        } catch (Exception ex) {
            Debug.WriteLine($"DB Error: {ex.Message}");
            return null;
        }
    }

    private async Task CloseActiveWindowAsync(long recordId, DateTime endTime) {
        try {
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
UPDATE active_window
SET end_time = @end_time
WHERE id = @id AND end_time IS NULL";
            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("end_time", endTime);
            cmd.Parameters.AddWithValue("id", recordId);
            _ = await cmd.ExecuteNonQueryAsync();
        } catch (Exception ex) {
            Debug.WriteLine($"DB Error: {ex.Message}");
        }
    }

    public async Task<string> GetRecordsJsonAsync() {
        try {
            if (string.IsNullOrWhiteSpace(connectionString)) return "[]";
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
SELECT
    'active_window' AS event_type,
    aw.window_title,
    aw.start_time,
    aw.end_time
FROM active_window aw
WHERE aw.pc_name_id = @pc_name_id
UNION ALL
SELECT
    'startup' AS event_type,
    'system' AS window_title,
    bs.boot_time AS start_time,
    NULL::timestamp AS end_time
FROM boot_shutdown bs
WHERE bs.pc_name_id = @pc_name_id
UNION ALL
SELECT
    'shutdown' AS event_type,
    'system' AS window_title,
    bs.shutdown_time AS start_time,
    NULL::timestamp AS end_time
FROM boot_shutdown bs
WHERE bs.pc_name_id = @pc_name_id
  AND bs.shutdown_time IS NOT NULL
ORDER BY start_time DESC
LIMIT 100";
            var pcNameId = await EnsurePcNameIdAsync();
            if (!pcNameId.HasValue) return "[]";
            var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
            var reader = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await reader.ReadAsync()) {
                results.Add(new {
                    window_title = reader["window_title"].ToString(),
                    event_type = reader["event_type"].ToString(),
                    start_time = reader["start_time"].ToString(),
                    end_time = reader["end_time"] == DBNull.Value ? "" : reader["end_time"].ToString()
                });
            }
            return JsonSerializer.Serialize(new { type = "records", data = results });
        } catch { return "{\"type\":\"records\",\"data\":[]}"; }
    }

    public async Task<string> GetDailyBootDurationJsonAsync() {
        try {
            if (string.IsNullOrWhiteSpace(connectionString)) return "{\"type\":\"bootDurations\",\"data\":[]}";
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
SELECT
    ds.date,
    COALESCE(SUM(EXTRACT(EPOCH FROM (COALESCE(bs.shutdown_time, @now) - bs.boot_time))), 0) / 3600 AS total_hours
FROM (
    SELECT (@today - (i || ' day')::interval)::date AS date
    FROM generate_series(0, 6) i
) ds
LEFT JOIN boot_shutdown bs ON DATE(bs.boot_time) = ds.date AND bs.pc_name_id = @pc_name_id
GROUP BY ds.date
ORDER BY ds.date ASC";
            var pcNameId = await EnsurePcNameIdAsync();
            if (!pcNameId.HasValue) return "{\"type\":\"bootDurations\",\"data\":[]}";
            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
            cmd.Parameters.AddWithValue("now", DateTime.Now);
            cmd.Parameters.AddWithValue("today", DateTime.Today);
            var reader = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await reader.ReadAsync()) {
                results.Add(new {
                    date = ((DateOnly)reader["date"]).ToString("yyyy-MM-dd"),
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
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            
            // 今日の0時から明日（今日+1日）の0時までの範囲で計算
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);
            var now = DateTime.Now;

            const string query = @"
SELECT
    window_title,
    SUM(EXTRACT(EPOCH FROM (LEAST(COALESCE(end_time, @now), @tomorrow) - GREATEST(start_time, @today)))) / 3600 AS duration_hours
FROM active_window
WHERE pc_name_id = @pc_name_id
  AND start_time < @tomorrow
  AND COALESCE(end_time, @now) > @today
GROUP BY window_title
HAVING SUM(EXTRACT(EPOCH FROM (LEAST(COALESCE(end_time, @now), @tomorrow) - GREATEST(start_time, @today)))) > 0
ORDER BY duration_hours DESC";

            var pcNameId = await EnsurePcNameIdAsync();
            if (!pcNameId.HasValue) return "{\"type\":\"activeWindowDurations\",\"data\":[]}";
            
            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("today", today);
            cmd.Parameters.AddWithValue("tomorrow", tomorrow);

            var reader = await cmd.ExecuteReaderAsync();
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
