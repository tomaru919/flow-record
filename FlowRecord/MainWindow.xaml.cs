using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using FlowRecord.Monitor;

namespace FlowRecord;

public partial class MainWindow : Window {
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr hRecipient, uint Flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr Handle);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private IntPtr _notificationHandle;
    private bool _isSleeping = false;

    private readonly MonitorService _monitorService;

    public bool IsExiting { get; set; } = false;

    private bool _isPausedOrStopped = false;

    public MainWindow() {
        InitializeComponent();
        SetStartup();

        _monitorService = new MonitorService();
        _monitorService.Initialize();
        _monitorService.Start();

        InitializeWebView();

        // タスクトレイ常駐起動でウィンドウを Show しなくても HWND を生成し、
        // OnSourceInitialized を発火させて WM_POWERBROADCAST のフックを有効化する
        new WindowInteropHelper(this).EnsureHandle();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnSourceInitialized(EventArgs e) {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
        _notificationHandle = RegisterSuspendResumeNotification(hwnd, DEVICE_NOTIFY_WINDOW_HANDLE);
        ApplyTitleBarTheme(hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WM_POWERBROADCAST) {
            switch (wParam.ToInt32()) {
                case PBT_APMSUSPEND:
                    _monitorService.CancelPendingWake();
                    if (!_isSleeping) {
                        _isSleeping = true;
                        MonitorService.RecordSleep(DateTime.Now);
                    }
                    break;
                case PBT_APMRESUMEAUTOMATIC:
                    if (_isSleeping) {
                        _isSleeping = false;
                        _monitorService.ScheduleWakeConfirmation(DateTime.Now);
                    }
                    break;
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e) {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_notificationHandle != IntPtr.Zero) {
            UnregisterSuspendResumeNotification(_notificationHandle);
            _notificationHandle = IntPtr.Zero;
        }
        base.OnClosed(e);
    }

    private async void InitializeWebView() {
        await webView.EnsureCoreWebView2Async();

        var userDataFolder = Path.Combine(AppContext.BaseDirectory, "wwwroot");

#if DEBUG
        webView.CoreWebView2.Navigate("http://localhost:5173");
#else
        webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.flowrecord",
            userDataFolder,
            CoreWebView2HostResourceAccessKind.Allow
        );
        webView.CoreWebView2.Navigate("https://app.flowrecord/index.html");
#endif
        webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
    }

    // トレイアイコンからウィンドウを開いたときにフロント側のデータを再取得させる
    public void RequestRefresh() {
        webView.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"refresh\"}");
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
        var message = e.TryGetWebMessageAsString();
        if (message == "getRecords") {
            var json = await _monitorService.GetRecordsJsonAsync();
            webView.CoreWebView2.PostWebMessageAsJson(json);
        } else if (message != null && message.StartsWith("getBootDurations")) {
            var parts = message.Split(':');
            var weekOffset = parts.Length > 1 && int.TryParse(parts[1], out var offset) ? offset : 0;
            var json = await _monitorService.GetDailyBootDurationJsonAsync(weekOffset);
            webView.CoreWebView2.PostWebMessageAsJson(json);
        } else if (message == "getActiveWindowDurations") {
            var json = await _monitorService.GetActiveWindowDurationJsonAsync();
            webView.CoreWebView2.PostWebMessageAsJson(json);
        }
    }

    // ×ボタンで終了させず、トレイ常駐（Hide）にする
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
        if (!IsExiting) {
            e.Cancel = true;
            Hide();
        }
    }

    // Exitボタン用：DBへ shutdown_time を確実に書く
    public async Task ShutdownAndSaveAsync(DateTime shutdownTime) {
        if (_isPausedOrStopped) return;
        _isPausedOrStopped = true;

        await _monitorService.RecordShutdownAndStopAsync(shutdownTime);
    }

    private static bool IsSystemDarkMode() {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return (int)(key?.GetValue("AppsUseLightTheme") ?? 1) == 0;
    }

    private static void ApplyTitleBarTheme(IntPtr hwnd) {
        int darkFlag = IsSystemDarkMode() ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
        if (e.Category == UserPreferenceCategory.General) {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) Dispatcher.Invoke(() => ApplyTitleBarTheme(hwnd));
        }
    }

    private static void SetStartup() {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
#if DEBUG
            key.DeleteValue("FlowRecord", false);
#else
            var currentModule = System.Diagnostics.Process.GetCurrentProcess().MainModule;
            if (currentModule?.FileName != null) {
                key.SetValue("FlowRecord", currentModule.FileName);
            }
#endif
        } catch { }
    }
}
