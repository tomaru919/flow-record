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
    private static string PendingPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowRecord",
            "pending_shutdown.json"
        );

    private sealed class PendingShutdownDto {
        public long id { get; set; }
        public DateTime shutdown_time { get; set; }
    }

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
                var pcNameId = await EnsurePcNameIdAsync().ConfigureAwait(false);
                if (!pcNameId.HasValue) return;
                Debug.WriteLine("pcのIDを取得する");

                _bootShutdownId = await CreateBootRecordAsync(DateTime.Now).ConfigureAwait(false);
                Debug.WriteLine($"boot shutdown id: {_bootShutdownId}");

                await ApplyPendingShutdownFileAsync().ConfigureAwait(false);

                // if (!File.Exists(PendingPath)) Debug.WriteLine("ファイルが存在しません。");

                // var json = JsonSerializer.Serialize(dto);
                // Debug.WriteLine(json);
                // File.WriteAllText(PendingPath, json);

                await MonitoringLoop(_cts.Token).ConfigureAwait(false);
            } catch (Exception ex) {
                // ここで落ちても監視ループは開始しない（DB前提アプリなので）
                Debug.WriteLine($"MonitorService.Start error: {ex}");
            }
        });

        // Task.Run(() => MonitoringLoop(_cts.Token));
    }

    // この関数必要？
    public async Task StopAsync() {
        _cts?.Cancel();
        await FlushCurrentWindowAsync(DateTime.Now);
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
                return $"{process.ProcessName} - {text}";
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

    // OSシャットダウン用：{ id, shutdown_time } をローカルに保存
    public void SaveShutdownPendingFile(DateTime shutdownTime) {
        if (!_bootShutdownId.HasValue) return; // 起動IDが無いなら保存できない

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);

            var dto = new PendingShutdownDto {
                id = _bootShutdownId.Value,
                shutdown_time = shutdownTime
            };

            var json = JsonSerializer.Serialize(dto);

            // できるだけ確実にディスクに書く
            using var fs = new FileStream(PendingPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var sw = new StreamWriter(fs, Encoding.UTF8);
            sw.Write(json);
            sw.Flush();
            fs.Flush(true);
        } catch {
            // OS終了中は失敗しても仕方ないので無視
        }
    }

    // 起動時：ファイルがあれば読み込み → id の行を UPDATE → 成功したらファイル削除
    private async Task ApplyPendingShutdownFileAsync() {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        if (!File.Exists(PendingPath)) {
            // Debug.WriteLine("ファイルが存在しない");
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
            return;
        }

        Debug.WriteLine("jsonファイルを確認");

        PendingShutdownDto? dto;
        try {
            var json = await File.ReadAllTextAsync(PendingPath, Encoding.UTF8);
            dto = JsonSerializer.Deserialize<PendingShutdownDto>(json);
            if (dto == null) return;
        } catch {
            Debug.WriteLine("jsonファイルを読み込み中にエラー");
            return;
        }

        try {
            var pcNameId = await EnsurePcNameIdAsync();
            if (!pcNameId.HasValue) return;

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            const string query = @"
UPDATE boot_shutdown
SET shutdown_time = @shutdown_time
WHERE id = @id
  AND pc_name_id = @pc_name_id
  AND shutdown_time IS NULL;";

            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("shutdown_time", dto.shutdown_time);
            cmd.Parameters.AddWithValue("id", dto.id);
            cmd.Parameters.AddWithValue("pc_name_id", pcNameId.Value);

            var affected = await cmd.ExecuteNonQueryAsync();

            // 更新できた時だけ削除（失敗したら次回また試す）
            if (affected > 0) {
                File.Delete(PendingPath);
            }
        } catch {
            // DBが使えないときは次回に回す（ファイルは残す）
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
            return JsonSerializer.Serialize(results);
        } catch { return "[]"; }
    }
}
