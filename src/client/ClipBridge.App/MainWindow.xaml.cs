using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipBridge.Core.Interfaces;
using ClipBridge.Windows;

namespace ClipBridge.App;

public partial class MainWindow : Window
{
    private const int HistoryLimit = 1000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly IClipboardRepository _clipboardRepository;
    private readonly ObservableCollection<ClipBridge.Core.Models.ClipboardItem> _historyItems = [];

    public MainWindow()
    {
        InitializeComponent();

        HistoryList.ItemsSource = _historyItems;
        _clipboardRepository = ((App)System.Windows.Application.Current).ClipboardRepository;
        RefreshHistory();

        _clipboardMonitor = new ClipboardMonitor(this);
        _clipboardMonitor.TextCaptured += OnTextCaptured;
        _clipboardMonitor.ClipboardReadFailed += OnClipboardReadFailed;
        _clipboardMonitor.ListenerFailed += OnListenerFailed;
    }

    private void OnTextCaptured(string text)
    {
        if (_clipboardRepository.AddText(text, "local") is null)
        {
            return;
        }

        RefreshHistory();
    }

    private void RefreshHistory()
    {
        _historyItems.Clear();
        foreach (var item in _clipboardRepository.GetRecentTextItems(HistoryLimit))
        {
            _historyItems.Add(item);
        }

        Title = $"ClipBridge - 跨设备剪贴板 · {_historyItems.Count} 条";
        EmptyHistoryText.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClipboardReadFailed() => Title = "ClipBridge - 跨设备剪贴板 · 暂时无法读取剪贴板";

    private void OnListenerFailed(Exception _) => Title = "ClipBridge - 跨设备剪贴板 · 无法启动文本剪贴板监听";

    private void HistoryToolTip_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ToolTip toolTip || toolTip.PlacementTarget is not UIElement target)
        {
            return;
        }

        var pointerPosition = target.PointToScreen(Mouse.GetPosition(target));
        var popupSource = PresentationSource.FromVisual(toolTip) as HwndSource;
        if (popupSource is null)
        {
            return;
        }

        // WPF flips MousePoint at screen edges. Reapply the requested top-left anchor
        // after its placement pass has completed.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            SetWindowPos(
                popupSource.Handle,
                IntPtr.Zero,
                (int)Math.Round(pointerPosition.X),
                (int)Math.Round(pointerPosition.Y),
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
