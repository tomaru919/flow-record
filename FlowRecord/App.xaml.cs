using System.Configuration;
using System.Data;
using System.Drawing;
using System.Windows;

namespace FlowRecord;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application {
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        _mainWindow = new MainWindow();

        var iconUri = new Uri("pack://application:,,,/app.ico");
        var iconStreamInfo = GetResourceStream(iconUri);
        Icon? icon = null;
        if (iconStreamInfo != null) {
            icon = new Icon(iconStreamInfo.Stream);
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
        if (_mainWindow != null) {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    private void OnExitClick(object? sender, EventArgs e) {
        _mainWindow?.IsExiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e) {
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }
}
