using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32; // Registry用
using FlowRecord.Monitor;
using System.Windows.Forms;
using System.Drawing;

namespace FlowRecord;

public partial class MainWindow : Window {
    private NotifyIcon? _notifyIcon;
    private readonly MonitorService _monitorService;
    private bool _isExiting = false;

    public MainWindow() {
        InitializeComponent();
        InitializeTrayIcon();
        SetStartup();

        _monitorService = new MonitorService();
        _monitorService.Initialize();
        _monitorService.Start();

        InitializeWebView();
    }

    private async void InitializeWebView() {
        // WebView2の環境を初期化
        await webView.EnsureCoreWebView2Async();

        // ビルド済みフロントエンドファイルのパス（実行ファイル直下の wwwroot フォルダを想定）
        var userDataFolder = Path.Combine(AppContext.BaseDirectory, "wwwroot");

#if DEBUG
        // 【開発時】ViteサーバーのURL
        webView.CoreWebView2.Navigate("http://localhost:5173");
#else
    // 【本番時】ローカルファイルを仮想ドメインとしてマッピング
    // これにより "https://app.flowrecord/index.html" でローカルファイルにアクセスできます
    // (CORSエラーなどを防ぐための推奨設定です)
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

    private void InitializeTrayIcon() {
        // リソースからアイコンのストリームを取得
        var iconUri = new Uri("pack://application:,,,/app.ico");
        var iconStreamInfo = System.Windows.Application.GetResourceStream(iconUri);

        // ストリームからSystem.Drawing.Iconを作成
        var icon = new Icon(iconStreamInfo.Stream);

        _notifyIcon = new NotifyIcon {
            Icon = icon,
            Visible = true,
            Text = "FlowRecord Monitor"
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open", null, (s, e) => ShowWindow());
        contextMenu.Items.Add("Exit", null, (s, e) => ExitApp());
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => ShowWindow();
    }

    private void ShowWindow() {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp() {
        _isExiting = true;
        _monitorService.Stop();
        _notifyIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
        if (!_isExiting) {
            e.Cancel = true;
            Hide();
        }
    }

    private static void SetStartup() {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
#if DEBUG
            // 【開発時 (Debug)
            // 開発用のパスが登録されていたら邪魔になるため、スタートアップから削除する
            key.DeleteValue("FlowRecord", false);
#else
        // 【本番時 (Release)】
        // 実行中のファイルのフルパスを取得して登録する
        var currentModule = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        if (currentModule?.FileName != null)
        {
            key.SetValue("FlowRecord", currentModule.FileName);
        }
#endif
        } catch { /* 無視 */ }
    }
}
