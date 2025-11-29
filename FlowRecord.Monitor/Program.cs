using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

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
    private static HttpClient httpClient = new HttpClient();
    private static string apiUrl = "http://localhost:3000/api/records";
    private static string pcName = Environment.MachineName;

    [SupportedOSPlatform("windows")]
    static async Task Main(string[] args)
    {
        Console.WriteLine("FlowRecord Monitor started...");
        Console.WriteLine($"PC Name: {pcName}");
        Console.WriteLine("Monitoring windows...\n");
        
        // PC起動イベントを記録
        await SendRecord("system", "startup", DateTime.Now, null);

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
                        await SendRecord(currentWindow, "window_close", windowStartTime, DateTime.Now);
                    }

                    // 新しいウィンドウの開始を記録
                    currentWindow = activeWindow;
                    windowStartTime = DateTime.Now;
                    await SendRecord(currentWindow, "window_open", windowStartTime, null);
                }

                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
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
        _ = SendRecord("system", eventType, DateTime.Now, null);
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
        _ = SendRecord("system", eventType, DateTime.Now, null);
    }

    static async Task SendRecord(string windowTitle, string eventType, DateTime startTime, DateTime? endTime)
    {
        try
        {
            var record = new
            {
                pc_name = pcName,
                window_title = windowTitle,
                event_type = eventType,
                start_time = startTime.ToString("o"),
                end_time = endTime?.ToString("o")
            };

            var json = JsonSerializer.Serialize(record);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await httpClient.PostAsync(apiUrl, content);
            
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"✓ {eventType}: {windowTitle}");
            }
            else
            {
                Console.WriteLine($"✗ API Error ({response.StatusCode}): {eventType}");
            }
        }
        catch (HttpRequestException)
        {
            // APIサーバーが起動していない場合は静かに失敗
            // 将来的にローカルファイルに保存する機能を追加予定
            Console.WriteLine($"⊗ {eventType}: {windowTitle} (API offline)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error: {ex.Message}");
        }
    }
}