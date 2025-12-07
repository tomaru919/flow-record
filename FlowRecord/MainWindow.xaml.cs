using System.Windows;
using Microsoft.Web.WebView2.Core;
using System.Windows.Forms; // トレイアイコン用
using Microsoft.Win32; // レジストリ用
using FlowRecord.Monitor;

namespace FlowRecord
{
    public partial class MainWindow : Window
    {
        // 修正1: 'NotifyIcon?' とし、初期値をnull許容にします
        private NotifyIcon? _notifyIcon;
        private readonly MonitorService _monitorService;
        private bool _isExiting = false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeTrayIcon();
            SetStartup();

            // 監視サービスの開始
            _monitorService = new MonitorService();
            _monitorService.Initialize();
            _monitorService.Start();

            InitializeWebView();
        }

        private async void InitializeWebView()
        {
            await webView.EnsureCoreWebView2Async();
            
            // 開発中はViteサーバーのURL、ビルド後はローカルファイル
            webView.Source = new Uri("http://localhost:5173"); 
            
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
        }

        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var message = e.TryGetWebMessageAsString();
            if (message == "getRecords")
            {
                var json = await _monitorService.GetRecordsJsonAsync();
                webView.CoreWebView2.PostWebMessageAsJson(json);
            }
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "FlowRecord Monitor"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open", null, (s, e) => ShowWindow());
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApp());
            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, e) => ShowWindow();
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _isExiting = true;
            _monitorService.Stop();
            
            // 修正2: nullチェックをしてからDisposeする
            _notifyIcon?.Dispose();
            
            System.Windows.Application.Current.Shutdown();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 閉じるボタンで終了せず隠す
            if (!_isExiting)
            {
                e.Cancel = true;
                Hide();
            }
        }
        
        private static void SetStartup()
        {
            try {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                
                // 修正3: MainModuleがnullでないことを確認してからアクセスする
                var currentModule = System.Diagnostics.Process.GetCurrentProcess().MainModule;
                if (currentModule?.FileName != null)
                {
                    key?.SetValue("FlowRecord", currentModule.FileName);
                }
            } catch { /* 権限エラー等は無視 */ }
        }
    }
}
