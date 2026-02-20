using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using FlowRecord.Monitor;
using System.Diagnostics;

namespace FlowRecord;

public partial class MainWindow : Window {
    private readonly MonitorService _monitorService;

    public bool IsExiting { get; set; } = false;

    private bool _isPausedOrStopped = false;

    public MainWindow() {
        InitializeComponent();
        SetStartup();

        _monitorService = new MonitorService();
        _monitorService.Initialize();

        // 電源状態（スリープ・復帰）の監視を開始
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // ★起動時の処理（boot INSERT → id確保 → pendingファイル読んで UPDATE → 監視開始）
        _monitorService.Start();

        InitializeWebView();
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

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
        var message = e.TryGetWebMessageAsString();
        if (message == "getRecords") {
            var json = await _monitorService.GetRecordsJsonAsync();
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

    // 最近のPCはスリープイベントが来ない場合でも、ロックイベントは来る場合が多い
    private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e) {
        switch (e.Reason) {
            case SessionSwitchReason.SessionLock:
                if (_isPausedOrStopped) return;
                _isPausedOrStopped = true;

                try {
                    await _monitorService.RecordSleepAsync(DateTime.Now);
                } catch (Exception ex) {
                    Debug.WriteLine($"停止処理エラー: {ex.Message}");
                }
                break;
            
            case SessionSwitchReason.SessionUnlock:
                if (!_isPausedOrStopped) return;
                _isPausedOrStopped = false;

                try {
                    _monitorService.Start();
                    await _monitorService.RecordWakeAsync(DateTime.Now);
                } catch (Exception ex) {
                    Debug.WriteLine($"再開処理エラー: {ex.Message}");
                }
                break;
            
            default:
                Debug.WriteLine($"その他のセッションイベント: {e.Reason}");
                break;
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
