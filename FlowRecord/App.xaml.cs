using System.Configuration;
using System.Data;
using System.Drawing;
using System.Windows;
using Microsoft.Win32;
using System.Text.Json;
using System.IO;

namespace FlowRecord;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application {
    private NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);

        SystemEvents.SessionEnding += OnSessionEnding;

        _mainWindow = new MainWindow();

        Icon? icon = null;
        try {
            var iconUri = new Uri("pack://application:,,,/app.ico");
            var iconStreamInfo = GetResourceStream(iconUri);
            if (iconStreamInfo != null) {
                icon = new Icon(iconStreamInfo.Stream);
            }
        } catch { }

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
        // if (_mainWindow != null) {
        //     _mainWindow.Show();
        //     _mainWindow.WindowState = WindowState.Normal;
        //     _mainWindow.Activate();
        // }

        if (_mainWindow == null) return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private async void OnExitClick(object? sender, EventArgs e) {
        // if (_mainWindow == null) {
        //     Shutdown();
        //     return;
        // }

        // _mainWindow.IsExiting = true;

        // try {
        //     await _mainWindow.ShutdownAndSaveAsync(DateTime.Now);
        // } catch { }

        if (_mainWindow != null) {
            _mainWindow.IsExiting = true;

            // Exitボタンは時間があるので、ここではDBへ確実に記録する
            try {
                await _mainWindow.ShutdownAndSaveAsync(DateTime.Now);
            } catch { }
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e) {
        // _notifyIcon?.Dispose();

        try {
            if (_notifyIcon != null) {
                _notifyIcon.Visible = false;
                _notifyIcon.DoubleClick -= OnOpenClick;
                _notifyIcon.ContextMenuStrip?.Dispose();
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        } catch { }

        SystemEvents.SessionEnding -= OnSessionEnding;
        base.OnExit(e);
    }

    private async void OnSessionEnding(object sender, SessionEndingEventArgs e) {
        if (_mainWindow == null) return;

        _mainWindow.IsExiting = true;

        try {
            await _mainWindow.ShutdownAndSaveAsync(DateTime.Now);
        } catch { }
    }
}
