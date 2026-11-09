using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace ClipBridge.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly Forms.ToolStripMenuItem _syncStatusItem;

    public TrayIconService()
    {
        var iconResource = WpfApplication.GetResourceStream(new Uri(
            "pack://application:,,,/ClipBridge;component/Assets/Icons/clipbridge.ico",
            UriKind.Absolute)) ?? throw new InvalidOperationException("The ClipBridge icon resource is unavailable.");
        using (iconResource.Stream)
        {
            _trayIcon = new Icon(iconResource.Stream);
        }

        var menu = new Forms.ContextMenuStrip();
        _syncStatusItem = new Forms.ToolStripMenuItem("同步：未连接") { Enabled = false };
        menu.Items.Add(_syncStatusItem);
        menu.Items.Add("连接同步…", null, (_, _) => ShowSyncConnectionWindow());
        var autoStartItem = new Forms.ToolStripMenuItem("开机时自动启动")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled()
        };
        autoStartItem.CheckedChanged += (_, _) => UpdateAutoStart(autoStartItem);
        menu.Items.Add(autoStartItem);
        menu.Items.Add("历史清理设置…", null, (_, _) => ShowHistoryRetentionWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开 ClipBridge", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ((App)WpfApplication.Current).ExitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "ClipBridge",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        ((App)WpfApplication.Current).SyncClient.StatusChanged += OnSyncStatusChanged;
    }

    public void Dispose()
    {
        ((App)WpfApplication.Current).SyncClient.StatusChanged -= OnSyncStatusChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }

    private void OnSyncStatusChanged(string status)
    {
        WpfApplication.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_notifyIcon.Visible)
            {
                return;
            }

            _syncStatusItem.Text = "同步：" + status;
        });
    }

    private static void ShowSyncConnectionWindow()
    {
        WpfApplication.Current.Dispatcher.Invoke(() => ((App)WpfApplication.Current).ShowSyncConnectionWindow());
    }

    private static void ShowHistoryRetentionWindow()
    {
        WpfApplication.Current.Dispatcher.Invoke(() => ((App)WpfApplication.Current).ShowHistoryRetentionWindow());
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

    private void UpdateAutoStart(Forms.ToolStripMenuItem item)
    {
        try
        {
            StartupRegistration.SetEnabled(item.Checked);
        }
        catch (Exception exception)
        {
            item.Checked = StartupRegistration.IsEnabled();
            _notifyIcon.BalloonTipTitle = "ClipBridge";
            _notifyIcon.BalloonTipText = $"无法更新登录启动设置：{exception.Message}";
            _notifyIcon.ShowBalloonTip(4000);
        }
    }
}
