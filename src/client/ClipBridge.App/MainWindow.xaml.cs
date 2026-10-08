using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipBridge.Core.Interfaces;
using ClipBridge.Core.Models;
using ClipBridge.Core.Services;
using ClipBridge.Windows;

namespace ClipBridge.App;

public partial class MainWindow : Window
{
    private const int HistoryLimit = 1000;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly GlobalHotkey _globalHotkey;
    private readonly IClipboardRepository _clipboardRepository;
    private readonly ObservableCollection<HistoryListEntry> _historyItems = [];
    private readonly ClipboardWriter _clipboardWriter = new();
    private readonly PasteManager _pasteManager = new();
    private readonly QuickPasteSelection _quickPasteSelection = new();
    private string? _lastSynchronizedClipboardItemId;
    private CancellationTokenSource? _synchronizedClipboardWriteCancellation;
    private bool _isQuickPasteMode;
    private nint _pasteTargetWindowHandle;
    private WindowState _savedWindowState;
    private WindowStartupLocation _savedWindowStartupLocation;
    private bool _savedShowInTaskbar;
    private bool _savedTopmost;
    private double _savedLeft;
    private double _savedTop;
    private double _savedWidth;
    private double _savedHeight;
    private bool _hasSavedNormalWindowState;
    private bool _hotkeyRegistrationFailed;
    private bool _allowApplicationExit;

    public MainWindow()
    {
        InitializeComponent();

        HistoryList.AddHandler(
            System.Windows.Controls.Control.MouseDoubleClickEvent,
            new MouseButtonEventHandler(HistoryList_MouseDoubleClick),
            handledEventsToo: true);
        HistoryList.ItemsSource = _historyItems;
        _clipboardRepository = ((App)System.Windows.Application.Current).ClipboardRepository;
        _lastSynchronizedClipboardItemId = _clipboardRepository.GetRecentItems(HistoryLimit)
            .Where(item => item.Remote)
            .MaxBy(item => item.CreatedAtUnixMilliseconds)?.Id;
        RefreshHistory();
        var syncClient = ((App)System.Windows.Application.Current).SyncClient;
        syncClient.HistoryUpdated += OnSynchronizedHistoryUpdated;

        _clipboardMonitor = new ClipboardMonitor(this);
        _clipboardMonitor.TextCaptured += OnTextCaptured;
        _clipboardMonitor.ImageCaptured += OnImageCaptured;
        _clipboardMonitor.ClipboardReadFailed += OnClipboardReadFailed;
        _clipboardMonitor.ListenerFailed += OnListenerFailed;

        _globalHotkey = new GlobalHotkey(this, ToggleQuickPasteMode);
        _globalHotkey.RegistrationFailed += OnHotkeyRegistrationFailed;
    }

    private void OnTextCaptured(string text)
    {
        CancelPendingSynchronizedClipboardWrite();
        var item = _clipboardRepository.AddText(text, Environment.MachineName);
        if (item is null)
        {
            return;
        }

        RefreshHistory();
        _ = PublishCapturedTextAsync(item);
    }

    private async Task PublishCapturedTextAsync(ClipBridge.Core.Models.ClipboardItem item)
    {
        try
        {
            await ((App)System.Windows.Application.Current).SyncClient.PublishTextAsync(item);
        }
        catch (HttpRequestException)
        {
            ((App)System.Windows.Application.Current).SyncClient.ReportStatus("Server 暂时不可用");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Failed to publish clipboard text: {0}", exception);
        }
    }

    private void OnSynchronizedHistoryUpdated()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(new Action(ApplySynchronizedHistoryUpdate));
        }
    }

    private void ApplySynchronizedHistoryUpdate()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        RefreshHistory();

        var remoteItem = _clipboardRepository.GetRecentItems(HistoryLimit)
            .Where(item => item.Remote)
            .MaxBy(item => item.CreatedAtUnixMilliseconds);
        if (remoteItem is null
            || string.Equals(_lastSynchronizedClipboardItemId, remoteItem.Id, StringComparison.Ordinal)
            || (remoteItem.Type == ClipboardItemType.Text && string.IsNullOrEmpty(remoteItem.TextContent))
            || (remoteItem.Type == ClipboardItemType.Image && string.IsNullOrEmpty(remoteItem.ImagePath)))
        {
            return;
        }

        _lastSynchronizedClipboardItemId = remoteItem.Id;
        var latestLocalItem = _clipboardRepository.GetRecentItems(HistoryLimit)
            .Where(item => !item.Remote)
            .MaxBy(item => item.CreatedAtUnixMilliseconds);
        if (latestLocalItem is not null
            && latestLocalItem.CreatedAtUnixMilliseconds >= remoteItem.CreatedAtUnixMilliseconds)
        {
            return;
        }

        CancelPendingSynchronizedClipboardWrite();
        var cancellation = new CancellationTokenSource();
        _synchronizedClipboardWriteCancellation = cancellation;
        _ = WriteSynchronizedClipboardItemAsync(remoteItem, cancellation);
    }

    private async Task WriteSynchronizedClipboardItemAsync(ClipboardItem item, CancellationTokenSource cancellation)
    {
        var text = item.Type == ClipboardItemType.Text ? item.TextContent : null;
        if (text is not null)
        {
            _clipboardMonitor.SuppressTextCapture(text);
        }

        try
        {
            switch (item.Type)
            {
                case ClipboardItemType.Text when !string.IsNullOrEmpty(text):
                    await _clipboardWriter.SetTextAsync(text, cancellation.Token);
                    break;
                case ClipboardItemType.Image when !string.IsNullOrEmpty(item.ImagePath):
                    await _clipboardWriter.SetImageAsync(item.ImagePath, cancellation.Token);
                    _clipboardMonitor.SuppressCurrentImageClipboardUpdate();
                    break;
                default:
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (text is not null)
            {
                _clipboardMonitor.CancelTextCaptureSuppression(text);
            }
        }
        catch (Exception exception)
        {
            if (text is not null)
            {
                _clipboardMonitor.CancelTextCaptureSuppression(text);
            }

            System.Diagnostics.Trace.TraceWarning(
                "Failed to copy synchronized clipboard item {0} to the system clipboard: {1}",
                item.Id,
                exception.Message);
            var description = item.Type == ClipboardItemType.Image ? "图片" : "文字";
            ((App)System.Windows.Application.Current).SyncClient.ReportStatus($"远端{description}已同步，暂时无法写入系统剪贴板");
        }
        finally
        {
            if (ReferenceEquals(_synchronizedClipboardWriteCancellation, cancellation))
            {
                _synchronizedClipboardWriteCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void OnImageCaptured(byte[] pngBytes)
    {
        CancelPendingSynchronizedClipboardWrite();
        try
        {
            _clipboardRepository.AddImage(pngBytes, "local");
            RefreshHistory();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Failed to save clipboard image: {0}", exception);
            Title = "ClipBridge - 跨设备剪贴板 · 图片保存失败";
        }
    }

    private void CancelPendingSynchronizedClipboardWrite()
    {
        var cancellation = _synchronizedClipboardWriteCancellation;
        _synchronizedClipboardWriteCancellation = null;
        cancellation?.Cancel();
    }

    private void RefreshHistory()
    {
        _historyItems.Clear();
        var query = HistorySearchBox.Text;
        var matchingItems = string.IsNullOrWhiteSpace(query)
            ? _clipboardRepository.GetRecentItems(HistoryLimit)
            : _clipboardRepository.SearchItems(query, HistoryLimit);
        var sequenceNumber = 0;
        foreach (var item in matchingItems)
        {
            _historyItems.Add(new HistoryListEntry(item, ++sequenceNumber));
        }

        if (_isQuickPasteMode)
        {
            _quickPasteSelection.SetItemCount(_historyItems.Count);
            HistoryList.SelectedIndex = _quickPasteSelection.SelectedIndex ?? -1;
        }

        var hotkeyStatus = _hotkeyRegistrationFailed ? " · 快捷键不可用" : string.Empty;
        Title = $"ClipBridge - 跨设备剪贴板{hotkeyStatus}";
        EmptyHistoryText.Text = string.IsNullOrWhiteSpace(query)
            ? "复制的文字或图片会显示在这里。"
            : "没有匹配的记录。";
        EmptyHistoryText.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholderText.Visibility = string.IsNullOrEmpty(HistorySearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshHistory();
    }

    private void OnClipboardReadFailed() => Title = "ClipBridge - 跨设备剪贴板 · 暂时无法读取剪贴板";

    private void OnListenerFailed(Exception _) => Title = "ClipBridge - 跨设备剪贴板 · 无法启动文本剪贴板监听";

    private void OnHotkeyRegistrationFailed(Exception _)
    {
        _hotkeyRegistrationFailed = true;
        Title = "ClipBridge - 跨设备剪贴板 · 快捷键不可用";
    }

    private async void ToggleQuickPasteMode()
    {
        if (_isQuickPasteMode)
        {
            await CloseQuickPasteModeAsync(restoreTargetFocus: true);
            return;
        }

        var targetWindowHandle = _pasteManager.GetForegroundWindowHandle();
        if (targetWindowHandle == nint.Zero || targetWindowHandle == new WindowInteropHelper(this).Handle)
        {
            return;
        }

        SaveNormalWindowStateIfNeeded();
        _pasteTargetWindowHandle = targetWindowHandle;
        _isQuickPasteMode = true;

        Hide();
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Topmost = true;
        HistorySearchBox.Clear();
        RefreshHistory();
        PositionAtCursor();
        if (!IsVisible)
        {
            Show();
        }

        if (HistoryList.SelectedItem is not null)
        {
            HistoryList.ScrollIntoView(HistoryList.SelectedItem);
        }

        Activate();
        HistorySearchBox.Focus();
        Keyboard.Focus(HistorySearchBox);
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isQuickPasteMode)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                MoveQuickPasteSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveQuickPasteSelection(1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await PasteSelectedItemAsync();
                break;
            case Key.Escape:
                e.Handled = true;
                await CloseQuickPasteModeAsync(restoreTargetFocus: true);
                break;
        }
    }

    private void MoveQuickPasteSelection(int delta)
    {
        HistoryList.SelectedIndex = _quickPasteSelection.Move(delta) ?? -1;
        HistoryList.ScrollIntoView(HistoryList.SelectedItem);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _quickPasteSelection.Select(HistoryList.SelectedIndex);

    private async void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_isQuickPasteMode || e.ChangedButton != MouseButton.Left
            || e.OriginalSource is not DependencyObject originalSource
            || ItemsControl.ContainerFromElement(HistoryList, originalSource) is not ListBoxItem listBoxItem
            || listBoxItem.DataContext is not HistoryListEntry entry)
        {
            return;
        }

        HistoryList.SelectedItem = entry;
        e.Handled = true;
        await PasteSelectedItemAsync();
    }

    private async Task PasteSelectedItemAsync()
    {
        if (HistoryList.SelectedItem is not HistoryListEntry entry)
        {
            return;
        }

        var item = entry.Item;

        var targetWindowHandle = _pasteTargetWindowHandle;
        Hide();
        LeaveQuickPasteMode();
        var succeeded = await PasteSelectedItemAsync(item, targetWindowHandle);
        if (succeeded)
        {
            _clipboardRepository.MarkUsed(item.Id);
            RefreshHistory();
            return;
        }

        System.Windows.MessageBox.Show(
            "没有完成粘贴。请确认原窗口仍然可用；内容仍保留在剪贴板中。",
            "ClipBridge",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private async Task<bool> PasteSelectedItemAsync(
        ClipBridge.Core.Models.ClipboardItem item,
        nint targetWindowHandle)
    {
        try
        {
            switch (item.Type)
            {
                case ClipBridge.Core.Models.ClipboardItemType.Text when !string.IsNullOrEmpty(item.TextContent):
                    _clipboardMonitor.SuppressTextCapture(item.TextContent);
                    await _clipboardWriter.SetTextAsync(item.TextContent);
                    break;
                case ClipBridge.Core.Models.ClipboardItemType.Image when !string.IsNullOrEmpty(item.ImagePath):
                    await _clipboardWriter.SetImageAsync(item.ImagePath);
                    _clipboardMonitor.SuppressCurrentImageClipboardUpdate();
                    break;
                default:
                    return false;
            }
        }
        catch (Exception exception) when (
            exception is ExternalException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (item.Type == ClipBridge.Core.Models.ClipboardItemType.Text && item.TextContent is not null)
            {
                _clipboardMonitor.CancelTextCaptureSuppression(item.TextContent);
            }

            System.Windows.MessageBox.Show(
                "当前剪贴板无法写入，请稍后重试。",
                "ClipBridge",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return false;
        }

        return await _pasteManager.PasteToAsync(targetWindowHandle);
    }

    private async Task CloseQuickPasteModeAsync(bool restoreTargetFocus)
    {
        var targetWindowHandle = _pasteTargetWindowHandle;
        Hide();
        LeaveQuickPasteMode();
        if (restoreTargetFocus)
        {
            await _pasteManager.RestoreForegroundWindowAsync(targetWindowHandle);
        }
    }

    private void LeaveQuickPasteMode()
    {
        if (!_isQuickPasteMode)
        {
            return;
        }

        _isQuickPasteMode = false;
        _pasteTargetWindowHandle = nint.Zero;
    }

    public void ShowHistoryWindow()
    {
        if (_isQuickPasteMode)
        {
            Hide();
            LeaveQuickPasteMode();
        }

        RestoreNormalWindowStateWhileHidden();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    public void AllowApplicationExit() => _allowApplicationExit = true;

    private void SaveNormalWindowStateIfNeeded()
    {
        if (_hasSavedNormalWindowState)
        {
            return;
        }

        _savedWindowState = WindowState;
        _savedWindowStartupLocation = WindowStartupLocation;
        _savedShowInTaskbar = ShowInTaskbar;
        _savedTopmost = Topmost;
        _savedLeft = Left;
        _savedTop = Top;
        _savedWidth = Width;
        _savedHeight = Height;
        _hasSavedNormalWindowState = true;
    }

    private void RestoreNormalWindowStateWhileHidden()
    {
        if (!_hasSavedNormalWindowState)
        {
            return;
        }

        WindowStartupLocation = _savedWindowStartupLocation;
        Topmost = _savedTopmost;
        ShowInTaskbar = _savedShowInTaskbar;
        Width = _savedWidth;
        Height = _savedHeight;
        Left = _savedLeft;
        Top = _savedTop;
        WindowState = _savedWindowState;
        _hasSavedNormalWindowState = false;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowApplicationExit)
        {
            return;
        }

        if (!_isQuickPasteMode)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        e.Cancel = true;
        await CloseQuickPasteModeAsync(restoreTargetFocus: true);
    }

    private void PositionAtCursor()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == nint.Zero || !GetCursorPos(out var cursorPosition)
            || !GetWindowRect(windowHandle, out var windowRect))
        {
            return;
        }

        var monitor = MonitorFromPoint(cursorPosition, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var windowWidth = windowRect.Right - windowRect.Left;
        var windowHeight = windowRect.Bottom - windowRect.Top;
        var workArea = monitorInfo.WorkArea;
        var left = Math.Clamp(cursorPosition.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - windowWidth));
        var top = Math.Clamp(cursorPosition.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - windowHeight));
        SetWindowPos(windowHandle, nint.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointNative point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo monitorInfo);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public RectNative MonitorArea;
        public RectNative WorkArea;
        public uint Flags;
    }
}
