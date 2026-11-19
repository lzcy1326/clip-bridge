using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace ClipBridge.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
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
        _menu = menu;
        menu.Renderer = new TrayMenuRenderer();
        _syncStatusItem = new Forms.ToolStripMenuItem("同步：未连接") { Enabled = false };
        menu.Items.Add(_syncStatusItem);
        menu.Items.Add("连接同步…", null, (_, _) => RunAfterMenuCloses(ShowSyncConnectionWindow));
        var automaticSync = new Forms.ToolStripMenuItem("自动同步本机复制和截图");
        menu.Opening += (_, _) =>
        {
            if (WpfApplication.Current.MainWindow is MainWindow main) automaticSync.Checked = main.AutomaticSyncEnabled;
        };
        automaticSync.Click += (_, _) => RunAfterMenuCloses(() =>
        {
            if (WpfApplication.Current.MainWindow is MainWindow main)
            {
                main.SetAutomaticSyncEnabled(!main.AutomaticSyncEnabled);
                automaticSync.Checked = main.AutomaticSyncEnabled;
            }
        });
        menu.Items.Add(automaticSync);
        menu.Items.Add("同步快捷键…", null, (_, _) => RunAfterMenuCloses(() =>
        {
            if (WpfApplication.Current.MainWindow is MainWindow window) window.ShowSyncShortcutSettings();
        }));
        var autoStartItem = new Forms.ToolStripMenuItem("开机时自动启动")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled()
        };
        autoStartItem.CheckedChanged += (_, _) => UpdateAutoStart(autoStartItem);
        menu.Items.Add(autoStartItem);
        menu.Items.Add("历史清理设置…", null, (_, _) => RunAfterMenuCloses(ShowHistoryRetentionWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("打开 ClipBridge", null, (_, _) => RunAfterMenuCloses(ShowMainWindow));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => RunAfterMenuCloses(() => ((App)WpfApplication.Current).ExitApplication()));

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
        if (_menu.Visible)
        {
            _menu.Close();
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _trayIcon.Dispose();
    }

    private void RunAfterMenuCloses(Action action)
    {
        // 菜单项的 Click 回调运行在这个下拉菜单自己的消息循环里。若在回调中同步执行
        // Shutdown（或弹出模态窗口），托盘图标/菜单被销毁时消息循环还没退出，会表现为
        // 点击“退出”无反应、菜单残留在桌面、进程无法结束。先关闭菜单让回调正常返回，
        // 再把动作排进 WPF 调度器，等消息循环退出后再执行。
        _menu.Close();
        WpfApplication.Current.Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
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
            if (WpfApplication.Current.MainWindow is MainWindow main && !main.AutomaticSyncEnabled
                && (status is "已同步" or "截图已同步"
                    || status.StartsWith("同步未完成", StringComparison.Ordinal)
                    || status.StartsWith("当前程序没有复制", StringComparison.Ordinal)
                    || status.StartsWith("剪贴板已变化", StringComparison.Ordinal)
                    || status.StartsWith("文件仅支持", StringComparison.Ordinal)
                    || status.StartsWith("图片超过", StringComparison.Ordinal)
                    || status.StartsWith("未连接同步服务", StringComparison.Ordinal)
                    || status.StartsWith("无法返回原窗口", StringComparison.Ordinal)
                    || status.StartsWith("当前剪贴板没有", StringComparison.Ordinal)
                    || status.StartsWith("设备凭证已失效", StringComparison.Ordinal)
                    || status.StartsWith("登录已过期", StringComparison.Ordinal)
                    || status.StartsWith("已尝试本机复制", StringComparison.Ordinal)))
            {
                _notifyIcon.BalloonTipTitle = "ClipBridge";
                _notifyIcon.BalloonTipText = status;
                _notifyIcon.ShowBalloonTip(3000);
            }
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
