using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Diagnostics;
using Microsoft.Win32;
using Npgsql;
using DotNetEnv;

[SupportedOSPlatform("windows")]
class FlowRecordMonitor
{
    // Windows API の宣言
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static string currentWindow = "";
    private static DateTime windowStartTime = DateTime.Now;
    private static string? connectionString;
    private static string pcName = Environment.MachineName;

    [SupportedOSPlatform("windows")]
    static async Task Main(string[] args)
    {
        try
        {
            // .envファイルをプロジェクトのルートから読み込む
            var envPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.env"));
            if (File.Exists(envPath))
            {
                Env.Load(envPath);
            }
            else
            {
                // .envファイルが見つからない場合、環境変数から直接読み込む
                Console.WriteLine($"Warning: .env file not found at {envPath}. Attempting to use environment variables.");
            }

            // 接続文字列を構築
            connectionString = $"Host={Environment.GetEnvironmentVariable("DB_HOST")};" +
                               $"Port={Environment.GetEnvironmentVariable("DB_PORT") ?? "5432"};" +
                               $"Username={Environment.GetEnvironmentVariable("DB_USER")};" +
                               $"Password={Environment.GetEnvironmentVariable("DB_PASSWORD")};" +
                               $"Database={Environment.GetEnvironmentVariable("DB_NAME")}";

            // データベース接続テスト
            await TestDatabaseConnection();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Critical Error during initialization: {ex.Message}");
            Console.WriteLine("Press any key to exit.");
            Console.ReadKey();
            return;
        }

        Console.WriteLine("FlowRecord Monitor started...");
        Console.WriteLine($"PC Name: {pcName}");
        Console.WriteLine("Monitoring windows...\n");
        
        // PC起動イベントを記録
        await SaveRecordToDbAsync("system", "startup", DateTime.Now, null);

        // システムイベント監視
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // ウィンドウ監視ループ
        while (true)
        {
            try
            {
                string activeWindow = GetActiveWindowTitle();
                
                if (activeWindow != currentWindow && !string.IsNullOrEmpty(activeWindow))
                {
                    // 前のウィンドウの終了時刻を記録
                    if (!string.IsNullOrEmpty(currentWindow))
                    {
                        await SaveRecordToDbAsync(currentWindow, "window_close", windowStartTime, DateTime.Now);
                    }

                    // 新しいウィンドウの開始を記録
                    currentWindow = activeWindow;
                    windowStartTime = DateTime.Now;
                    await SaveRecordToDbAsync(currentWindow, "window_open", windowStartTime, null);
                }

                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in main loop: {ex.Message}");
            }
        }
    }
    
    static async Task TestDatabaseConnection()
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            Console.WriteLine("✓ Successfully connected to PostgreSQL database.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Failed to connect to PostgreSQL database: {ex.Message}");
            throw;
        }
    }


    [SupportedOSPlatform("windows")]
    static string GetActiveWindowTitle()
    {
        IntPtr handle = GetForegroundWindow();
        StringBuilder text = new StringBuilder(256);

        if (GetWindowText(handle, text, 256) > 0)
        {
            GetWindowThreadProcessId(handle, out uint processId);
            try
            {
                Process process = Process.GetProcessById((int)processId);
                return $"{process.ProcessName} - {text}";
            }
            catch
            {
                return text.ToString();
            }
        }

        return "";
    }

    [SupportedOSPlatform("windows")]
    static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        string eventType = e.Reason switch
        {
            SessionSwitchReason.SessionLock => "lock",
            SessionSwitchReason.SessionUnlock => "unlock",
            SessionSwitchReason.SessionLogoff => "logoff",
            SessionSwitchReason.SessionLogon => "logon",
            _ => "unknown"
        };

        Console.WriteLine($"Session event: {eventType}");
        _ = SaveRecordToDbAsync("system", eventType, DateTime.Now, null);
    }

    [SupportedOSPlatform("windows")]
    static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        string eventType = e.Mode switch
        {
            PowerModes.Suspend => "sleep",
            PowerModes.Resume => "resume",
            _ => "unknown"
        };

        Console.WriteLine($"Power event: {eventType}");
        _ = SaveRecordToDbAsync("system", eventType, DateTime.Now, null);
    }

    static async Task SaveRecordToDbAsync(string windowTitle, string eventType, DateTime startTime, DateTime? endTime)
    {
        try
        {
            // duration_secondsを計算（end_timeがある場合）
            int? durationSeconds = null;
            if (endTime.HasValue)
            {
                durationSeconds = (int)Math.Floor((endTime.Value - startTime).TotalSeconds);
            }

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            const string query = @"
                INSERT INTO records (pc_name, window_title, event_type, start_time, end_time, duration_seconds)
                VALUES (@pc_name, @window_title, @event_type, @start_time, @end_time, @duration_seconds)";

            await using var cmd = new NpgsqlCommand(query, conn);
            
            cmd.Parameters.AddWithValue("pc_name", pcName);
            cmd.Parameters.AddWithValue("window_title", windowTitle);
            cmd.Parameters.AddWithValue("event_type", eventType);
            cmd.Parameters.AddWithValue("start_time", startTime);
            cmd.Parameters.AddWithValue("end_time", endTime.HasValue ? (object)endTime.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("duration_seconds", durationSeconds.HasValue ? (object)durationSeconds.Value : DBNull.Value);
            
            await cmd.ExecuteNonQueryAsync();

            Console.WriteLine($"✓ DB {eventType}: {windowTitle}");
        }
        catch (NpgsqlException ex)
        {
            Console.WriteLine($"✗ DB Error: {ex.Message}");
            // 将来的にローカルファイルに保存する機能を追加予定
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error: {ex.Message}");
        }
    }
}