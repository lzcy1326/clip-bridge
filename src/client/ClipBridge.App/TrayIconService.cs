using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace ClipBridge.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;

    public TrayIconService()
    {
        var iconResource = WpfApplication.GetResourceStream(new Uri(
            "pack://application:,,,/ClipBridge.App;component/Assets/Icons/clipbridge.ico",
            UriKind.Absolute)) ?? throw new InvalidOperationException("The ClipBridge icon resource is unavailable.");
        using (iconResource.Stream)
        {
            _trayIcon = new Icon(iconResource.Stream);
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 ClipBridge", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "ClipBridge",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }

    private static void ShowMainWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var window = System.Windows.Application.Current.MainWindow;
            if (window is null)
            {
                return;
            }

            if (window is MainWindow mainWindow)
            {
                mainWindow.ShowHistoryWindow();
                return;
            }

            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        });
    }
}
