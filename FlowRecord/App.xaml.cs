using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using Microsoft.Win32;
using System.IO;

namespace FlowRecord;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application {
    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowRecord",
            "shutdown.txt"
        );

    private static void AppendLine(string line) {
        try {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);

            using var fs = new FileStream(
                LogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough
            );
            using var sw = new StreamWriter(fs);
            sw.WriteLine(line);
            sw.Flush();
            fs.Flush(true);
        } catch {
            // まずは握りつぶしてOK。必要なら Debug.WriteLine で出す
        }
    }

    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        // PCシャットダウン/ログオフ通知
        SessionEnding += OnSessionEnding;

        _mainWindow = new MainWindow();

        Icon? icon = null;
        try {
            var iconUri = new Uri("pack://application:,,,/app.ico");
            var iconStreamInfo = GetResourceStream(iconUri);
            if (iconStreamInfo != null) {
                icon = new Icon(iconStreamInfo.Stream);
            }
        } catch {
            Debug.WriteLine("アイコンの設定に失敗");
        }

        _notifyIcon = new NotifyIcon {
            Icon = icon,
            Visible = true,
            Text = "FlowRecord"
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open", null, OnOpenClick);
        contextMenu.Items.Add("Exit", null, OnExitClick);
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += OnOpenClick;
    }

    private void OnOpenClick(object? sender, EventArgs e) {
        if (_mainWindow == null) return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    // Exitボタンは時間があるので、従来どおりDBへ確実に書く
    private async void OnExitClick(object? sender, EventArgs e) {
        if (_mainWindow != null) {
            _mainWindow.IsExiting = true;

            try {
                await _mainWindow.ShutdownAndSaveAsync(DateTime.Now);
            } catch { }
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e) {
        try {
            if (_notifyIcon != null) {
                _notifyIcon.Visible = false;
                _notifyIcon.DoubleClick -= OnOpenClick;
                _notifyIcon.ContextMenuStrip?.Dispose();
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        } catch { }

        SessionEnding -= OnSessionEnding;
        base.OnExit(e);
    }

    // ★ここが重要：PCシャットダウン中はDBを触らない
    // 代わりにローカルファイルへ { id, shutdown_time } を保存するだけ
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) {
        AppendLine($"{DateTime.Now}");

        if (_mainWindow == null) return;

        _mainWindow.IsExiting = true;
    }
}
