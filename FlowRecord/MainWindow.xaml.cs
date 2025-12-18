using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32; // Registry用
using FlowRecord.Monitor;

namespace FlowRecord;

public partial class MainWindow : Window {
    private readonly MonitorService _monitorService;
    public bool IsExiting { get; set; } = false;

    public MainWindow() {
        InitializeComponent();
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

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) {
        if (!IsExiting) {
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
