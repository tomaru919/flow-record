using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DotNetEnv;
using Microsoft.Win32;
using Npgsql;

namespace FlowRecord.Monitor
{

public class MonitorService
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private string currentWindow = "";
    private DateTime windowStartTime = DateTime.Now;
    private string? connectionString;
    private readonly string pcName = Environment.MachineName;
    private CancellationTokenSource? _cts;

    public void Initialize()
    {
        var envPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".env"));
        if (File.Exists(envPath)) Env.Load(envPath);

        connectionString = $"User Id={Environment.GetEnvironmentVariable("SUPABASE_USER")};" +
                            $"Password={Environment.GetEnvironmentVariable("SUPABASE_PASSWORD")};" +
                            $"Server={Environment.GetEnvironmentVariable("SUPABASE_SERVER")};" +
                            $"Port=5432;" +
                            $"Database={Environment.GetEnvironmentVariable("SUPABASE_DB")};" +
                            "SSL Mode=Require;Trust Server Certificate=true";
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => MonitoringLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        // 終了時に最後のウィンドウを記録
        _ = SaveRecordToDbAsync(currentWindow, "window_close", windowStartTime, DateTime.Now);
    }

    private async Task MonitoringLoop(CancellationToken token)
    {
        // PC起動イベント
        await SaveRecordToDbAsync("system", "startup", DateTime.Now, null);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        while (!token.IsCancellationRequested)
        {
            try
            {
                string activeWindow = GetActiveWindowTitle();
                if (activeWindow != currentWindow && !string.IsNullOrEmpty(activeWindow))
                {
                    if (!string.IsNullOrEmpty(currentWindow))
                    {
                        await SaveRecordToDbAsync(currentWindow, "window_close", windowStartTime, DateTime.Now);
                    }
                    currentWindow = activeWindow;
                    windowStartTime = DateTime.Now;
                    await SaveRecordToDbAsync(currentWindow, "window_open", windowStartTime, null);
                }
                await Task.Delay(1000, token);
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex) { Debug.WriteLine($"Error: {ex.Message}"); }
        }

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private static string GetActiveWindowTitle()
    {
        IntPtr handle = GetForegroundWindow();
        StringBuilder text = new(256);
        if (GetWindowText(handle, text, 256) > 0)
        {
            _ = GetWindowThreadProcessId(handle, out uint processId);
            try
            {
                Process process = Process.GetProcessById((int)processId);
                return $"{process.ProcessName} - {text}";
            }
            catch { return text.ToString(); }
        }
        return "";
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        string eventType = e.Reason switch
        {
            SessionSwitchReason.SessionLock => "lock",
            SessionSwitchReason.SessionUnlock => "unlock",
            SessionSwitchReason.SessionLogoff => "logoff",
            SessionSwitchReason.SessionLogon => "logon",
            _ => "unknown"
        };
        _ = SaveRecordToDbAsync("system", eventType, DateTime.Now, null);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        string eventType = e.Mode switch
        {
            PowerModes.Suspend => "sleep",
            PowerModes.Resume => "resume",
            _ => "unknown"
        };
        _ = SaveRecordToDbAsync("system", eventType, DateTime.Now, null);
    }

    // DB保存メソッド
    private async Task SaveRecordToDbAsync(
        string windowTitle,
        string eventType,
        DateTime startTime,
        DateTime? endTime
    )
    {
        try
        {
            int? durationSeconds = null;
            if (endTime.HasValue) durationSeconds = (int)Math.Floor((endTime.Value - startTime).TotalSeconds);

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
            INSERT INTO records (pc_name, window_title, event_type, start_time, end_time, duration_seconds)
            VALUES (@pc_name, @window_title, @event_type, @start_time, @end_time, @duration_seconds)";

            await using var cmd = new NpgsqlCommand(query, conn);
            cmd.Parameters.AddWithValue("pc_name", pcName);
            cmd.Parameters.AddWithValue("window_title", windowTitle ?? "");
            cmd.Parameters.AddWithValue("event_type", eventType);
            cmd.Parameters.AddWithValue("start_time", startTime);
            cmd.Parameters.AddWithValue("end_time", endTime.HasValue ? endTime.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("duration_seconds", durationSeconds.HasValue ? durationSeconds.Value : DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Debug.WriteLine($"DB Error: {ex.Message}"); }
    }

    // フロントエンド用データ取得メソッド
    public async Task<string> GetRecordsJsonAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            // 直近100件などを取得
            var cmd = new NpgsqlCommand("SELECT * FROM records ORDER BY start_time DESC LIMIT 100", conn);
            var reader = await cmd.ExecuteReaderAsync();
            var results = new List<object>();
            while (await reader.ReadAsync())
            {
                results.Add(
                    new
                    {
                        window_title = reader["window_title"].ToString(),
                        event_type = reader["event_type"].ToString(),
                        start_time = reader["start_time"].ToString(),
                        end_time = reader["end_time"] == DBNull.Value ? "" : reader["end_time"].ToString(),
                        duration = reader["duration_seconds"] == DBNull.Value ? null : reader["duration_seconds"]
                    }
                );
            }
            return System.Text.Json.JsonSerializer.Serialize(results);
        }
        catch { return "[]"; }
    }
}

}
