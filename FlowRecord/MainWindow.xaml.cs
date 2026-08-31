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
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    // Modern Standby (S0 Low Power Idle) 専用機では PBT_APMSUSPEND/RESUMEAUTOMATIC が届かないため、
    // Microsoft推奨のGUID_SYSTEM_AWAYMODE電源設定通知を併用してスリープ/復帰を検知する
    private static readonly Guid GUID_SYSTEM_AWAYMODE = new("98a7f580-01f7-48aa-9c0f-44352c29e5c0");

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr hRecipient, uint Flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr Handle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private IntPtr _notificationHandle;
    private IntPtr _awayModeNotificationHandle;
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
        var awayModeGuid = GUID_SYSTEM_AWAYMODE;
        _awayModeNotificationHandle = RegisterPowerSettingNotification(hwnd, ref awayModeGuid, DEVICE_NOTIFY_WINDOW_HANDLE);
        ApplyTitleBarTheme(hwnd);
    }

    private void HandleSuspend() {
        _monitorService.CancelPendingWake();
        if (!_isSleeping) {
            _isSleeping = true;
            _monitorService.RecordSleep(DateTime.Now);
        }
    }

    private void HandleResume() {
        if (_isSleeping) {
            _isSleeping = false;
            _monitorService.ScheduleWakeConfirmation(DateTime.Now);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg == WM_POWERBROADCAST) {
            switch (wParam.ToInt32()) {
                case PBT_APMSUSPEND:
                    HandleSuspend();
                    break;
                case PBT_APMRESUMEAUTOMATIC:
                    HandleResume();
                    break;
                case PBT_POWERSETTINGCHANGE:
                    var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                    if (setting.PowerSetting == GUID_SYSTEM_AWAYMODE) {
                        // Data == 1: away modeに入る（スリープ開始）, 0: away modeを抜ける（復帰）
                        if (setting.Data == 1) {
                            HandleSuspend();
                        } else {
                            HandleResume();
                        }
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
        if (_awayModeNotificationHandle != IntPtr.Zero) {
            UnregisterPowerSettingNotification(_awayModeNotificationHandle);
            _awayModeNotificationHandle = IntPtr.Zero;
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

    // OSシャットダウン/ログオフ通知用：DBへ shutdown_time を直接書く（同期）
    public void RecordShutdownSync(DateTime shutdownTime) {
        _monitorService.RecordShutdownSync(shutdownTime);
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
