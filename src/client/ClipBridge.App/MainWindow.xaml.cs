using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
    private readonly SyncHotkey _syncHotkey;
    private readonly string _shortcutSettingsPath;
    private SyncShortcutSettings _shortcutSettings;
    private int _manualSyncRunning;
    private uint _autoEnableSequence;
    private readonly LocalClipboardHistory _localHistory;
    private readonly ClipboardEchoGuard _echoGuard = new();
    private int _historyRefreshQueued;
    private bool _isPasting;
    private long _lastLocalClipboardChangeAt;
    private readonly ObservableCollection<HistoryListEntry> _historyItems = [];
    private readonly ClipboardWriter _clipboardWriter = new();
    private readonly ClipboardWriter _synchronizedClipboardWriter = new();
    private readonly PasteManager _pasteManager = new();
    private readonly CopySelectionCommand _copySelection = new();
    private readonly QuickPasteSelection _quickPasteSelection = new();
    private string? _lastSynchronizedClipboardItemId;
    private long _lastSynchronizedClipboardItemAt;
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
        _localHistory = ((App)System.Windows.Application.Current).LocalHistory;
        _localHistory.Changed += OnLocalHistoryChanged;
        RefreshHistory();
        var syncClient = ((App)System.Windows.Application.Current).SyncClient;
        syncClient.HistoryUpdated += OnSynchronizedHistoryUpdated;
        syncClient.RemoteClipboardReceived += OnRemoteClipboardReceived;

        _clipboardMonitor = new ClipboardMonitor(this);
        _clipboardMonitor.TextCaptured += OnTextCaptured;
        _clipboardMonitor.ImageCaptured += OnImageCaptured;
        _clipboardMonitor.FilesCaptured += OnFilesCaptured;
        _clipboardMonitor.ClipboardChanged += OnLocalClipboardChanged;
        _clipboardWriter.Written += _clipboardMonitor.SuppressSequence;
        _synchronizedClipboardWriter.Written += _clipboardMonitor.SuppressSequence;
        _clipboardMonitor.ClipboardReadFailed += OnClipboardReadFailed;
        _clipboardMonitor.ListenerFailed += OnListenerFailed;

        _globalHotkey = new GlobalHotkey(this, ToggleQuickPasteMode);
        _globalHotkey.RegistrationFailed += OnHotkeyRegistrationFailed;

        _shortcutSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBridge", "sync-shortcut.json");
        _shortcutSettings = SyncShortcutSettings.Load(_shortcutSettingsPath);
        syncClient.SetAutomaticUploadEnabled(_shortcutSettings.Automatic);
        _syncHotkey = new SyncHotkey(this, StartManualSync);
        _syncHotkey.RegistrationFailed += message => Dispatcher.BeginInvoke(new Action(() => syncClient.ReportStatus(message)));
        if (!_syncHotkey.Configure(!_shortcutSettings.Automatic, _shortcutSettings.Modifiers, _shortcutSettings.VirtualKey, out var shortcutError))
            Dispatcher.BeginInvoke(new Action(() => syncClient.ReportStatus(shortcutError ?? "同步快捷键不可用")));
    }

    private void OnTextCaptured(string text)
    {
        _echoGuard.RecordText(text);
        _localHistory.CaptureText(text, Environment.MachineName, IsAutomaticCaptureEligible());
    }

    private void OnFilesCaptured(string[] paths) => _localHistory.CaptureFiles(paths, Environment.MachineName);

    private void OnLocalClipboardChanged()
    {
        _lastLocalClipboardChangeAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        CancelPendingSynchronizedClipboardWrite();
    }

    private void OnSynchronizedHistoryUpdated() => _localHistory.Refresh();

    private void OnLocalHistoryChanged()
    {
        if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _historyRefreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _historyRefreshQueued, 0);
            RefreshHistory();
        }));
    }

    private void OnRemoteClipboardReceived(ClipboardItem item)
    {
        var sequence = ClipboardWriter.CurrentSequenceNumber;
        // A pending Windows clipboard notification can precede our UI callback.
        // Never overwrite a copy which the local monitor has not observed yet.
        if (sequence != _clipboardMonitor.ObservedSequence) return;
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ApplySynchronizedHistoryUpdate(item, sequence)));
    }

    private void ApplySynchronizedHistoryUpdate(ClipboardItem remoteItem, uint expectedSequence)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_isQuickPasteMode || _isPasting || Volatile.Read(ref _manualSyncRunning) != 0) return;
        if (_echoGuard.IsRecentLocal(remoteItem)) return;
        if (remoteItem.CreatedAtUnixMilliseconds < _lastSynchronizedClipboardItemAt
            || remoteItem.CreatedAtUnixMilliseconds == _lastSynchronizedClipboardItemAt
            && string.Equals(_lastSynchronizedClipboardItemId, remoteItem.Id, StringComparison.Ordinal)
            || (remoteItem.Type == ClipboardItemType.Text && string.IsNullOrEmpty(remoteItem.TextContent))
            || (remoteItem.Type == ClipboardItemType.Image && string.IsNullOrEmpty(remoteItem.ImagePath)))
        {
            return;
        }

        _lastSynchronizedClipboardItemId = remoteItem.Id;
        _lastSynchronizedClipboardItemAt = remoteItem.CreatedAtUnixMilliseconds;
        var syncClient = ((App)System.Windows.Application.Current).SyncClient;
        var latestLocalItem = _localHistory.Snapshot().Select(entry => entry.Item)
            .Where(item => !item.Remote).MaxBy(item => item.CreatedAtUnixMilliseconds);
        var latestLocalCopyAt = Math.Max(_lastLocalClipboardChangeAt,
            latestLocalItem?.CreatedAtUnixMilliseconds ?? 0);
        if (!RemoteClipboardWritePriority.IsNewerThanLocalCopy(remoteItem.CreatedAtUnixMilliseconds,
                latestLocalCopyAt, syncClient.ServerClockOffsetMilliseconds,
                syncClient.ServerClockUncertaintyMilliseconds))
        {
            return;
        }

        CancelPendingSynchronizedClipboardWrite();
        var cancellation = new CancellationTokenSource();
        _synchronizedClipboardWriteCancellation = cancellation;
        _ = WriteSynchronizedClipboardItemAsync(remoteItem, cancellation, expectedSequence);
    }

    private async Task WriteSynchronizedClipboardItemAsync(ClipboardItem item, CancellationTokenSource cancellation, uint expectedSequence)
    {
        var text = item.Type == ClipboardItemType.Text ? item.TextContent : null;

        try
        {
            switch (item.Type)
            {
                case ClipboardItemType.Text when !string.IsNullOrEmpty(text):
                    await _synchronizedClipboardWriter.SetTextAsync(text, cancellation.Token, expectedSequence);
                    break;
                case ClipboardItemType.Image when !string.IsNullOrEmpty(item.ImagePath):
                    await _synchronizedClipboardWriter.SetImageAsync(item.ImagePath, cancellation.Token, expectedSequence);
                    break;
                default:
                    return;
            }
        }
        catch (OperationCanceledException)
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
        _echoGuard.RecordImage(pngBytes);
        _localHistory.CaptureImage(pngBytes, Environment.MachineName, IsAutomaticCaptureEligible());
    }

    private bool IsAutomaticCaptureEligible() =>
        ((App)System.Windows.Application.Current).SyncClient.AutomaticUploadEnabled
        && _clipboardMonitor.CaptureSequence != Volatile.Read(ref _autoEnableSequence);

    private void CancelPendingSynchronizedClipboardWrite()
    {
        var cancellation = _synchronizedClipboardWriteCancellation;
        _synchronizedClipboardWriteCancellation = null;
        cancellation?.Cancel();
    }

    private void RefreshHistory()
    {
        if (_localHistory is null) return; // TextChanged can fire during InitializeComponent.
        var previousSelection = (HistoryList.SelectedItem as HistoryListEntry)?.Item;
        _historyItems.Clear();
        var query = HistorySearchBox.Text;
        var matchingItems = _localHistory.Snapshot(query);
        var sequenceNumber = 0;
        foreach (var item in matchingItems)
        {
            _historyItems.Add(new HistoryListEntry(item.Item, ++sequenceNumber, item.ImageBytes));
        }

        if (_isQuickPasteMode)
        {
            _quickPasteSelection.SetItemCount(_historyItems.Count);
            HistoryList.SelectedIndex = _quickPasteSelection.SelectedIndex ?? -1;
        }
        if (previousSelection is not null && _historyItems.FirstOrDefault(entry => entry.Item.Id == previousSelection.Id
            || entry.Item.Type == previousSelection.Type && entry.Item.ContentHash == previousSelection.ContentHash) is { } selected)
            HistoryList.SelectedItem = selected;

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

    private void OnClipboardReadFailed()
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Title = "ClipBridge - 跨设备剪贴板 · 暂时无法读取剪贴板"));
    }

    private void OnListenerFailed(Exception _) => Title = "ClipBridge - 跨设备剪贴板 · 无法启动文本剪贴板监听";

    private void OnHotkeyRegistrationFailed(Exception _)
    {
        _hotkeyRegistrationFailed = true;
        Title = "ClipBridge - 跨设备剪贴板 · 快捷键不可用";
    }

    public void ShowSyncShortcutSettings()
    {
        var dialog = new SyncShortcutWindow(_shortcutSettings, ApplySyncShortcutSettings) { Owner = IsVisible ? this : null };
        _syncHotkey.Suspend();
        try { dialog.ShowDialog(); }
        finally
        {
            if (!_syncHotkey.Configure(!_shortcutSettings.Automatic, _shortcutSettings.Modifiers, _shortcutSettings.VirtualKey, out var error))
                ((App)System.Windows.Application.Current).SyncClient.ReportStatus(error ?? "同步快捷键不可用");
        }
    }

    public bool AutomaticSyncEnabled => _shortcutSettings.Automatic;

    public void SetAutomaticSyncEnabled(bool enabled)
    {
        var error = ApplySyncShortcutSettings(_shortcutSettings with { Automatic = enabled });
        if (error is not null)
            System.Windows.MessageBox.Show(error, "ClipBridge", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string? ApplySyncShortcutSettings(SyncShortcutSettings settings)
    {
        if (!_syncHotkey.Configure(!settings.Automatic, settings.Modifiers, settings.VirtualKey, out var error)) return error;
        try { settings.Save(_shortcutSettingsPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _syncHotkey.Configure(!_shortcutSettings.Automatic, _shortcutSettings.Modifiers, _shortcutSettings.VirtualKey, out _);
            return "无法保存设置：" + exception.Message;
        }
        if (settings.Automatic && !_shortcutSettings.Automatic)
            Volatile.Write(ref _autoEnableSequence, ClipboardWriter.CurrentSequenceNumber);
        _shortcutSettings = settings;
        ((App)System.Windows.Application.Current).SyncClient.SetAutomaticUploadEnabled(settings.Automatic);
        return null;
    }

    private async void StartManualSync()
    {
        if (_shortcutSettings.Automatic || Interlocked.Exchange(ref _manualSyncRunning, 1) != 0) return;
        OnLocalClipboardChanged();
        var app = (App)System.Windows.Application.Current;
        try
        {
            ClipboardSnapshot snapshot;
            if (_isQuickPasteMode && IsActive)
            {
                if (HistoryList.SelectedItem is not HistoryListEntry { Type: ClipboardItemType.Image } selected)
                {
                    app.SyncClient.ReportStatus("请先在历史列表中选择要同步的图片");
                    return;
                }
                var image = selected.ImageBytes ?? (!string.IsNullOrEmpty(selected.ImagePath)
                    ? await File.ReadAllBytesAsync(selected.ImagePath) : null);
                if (image is null)
                {
                    app.SyncClient.ReportStatus("无法读取所选图片");
                    return;
                }
                snapshot = new ClipboardSnapshot(ClipboardSnapshotKind.Image, 0, Image: image);
            }
            else
            {
                var copy = await _copySelection.CopyAsync((int)_shortcutSettings.VirtualKey);
                if (!app.SyncClient.IsConnected)
                {
                    app.SyncClient.ReportStatus("已尝试本机复制；未连接同步服务，未发送");
                    return;
                }
                snapshot = await _clipboardMonitor.ReadCurrentAsync();
                var decision = CopySelectionCommand.Evaluate(copy, snapshot);
                if (decision == CopySnapshotResult.Changed)
                {
                    app.SyncClient.ReportStatus("剪贴板已变化，请重新使用同步快捷键");
                    return;
                }
                if (decision == CopySnapshotResult.NoNewCopy)
                {
                    app.SyncClient.ReportStatus("当前程序没有复制新内容；请选择内容后重试");
                    return;
                }
                if (decision == CopySnapshotResult.Files)
                {
                    app.SyncClient.ReportStatus("文件仅支持本机复制，暂不支持跨设备同步");
                    return;
                }
                if (decision == CopySnapshotResult.Unsupported)
                {
                    app.SyncClient.ReportStatus("当前剪贴板没有可同步的文字或图片");
                    return;
                }
            }
            if (!app.SyncClient.IsConnected)
            {
                app.SyncClient.ReportStatus("未连接同步服务，未发送");
                return;
            }
            if (snapshot.Kind == ClipboardSnapshotKind.Image && snapshot.Image!.Length > 2 * 1024 * 1024)
            {
                app.SyncClient.ReportStatus("图片超过 2 MiB，未同步");
                return;
            }
            app.SyncClient.ReportStatus("正在同步选中的内容");
            await Task.Run(async () =>
            {
                if (snapshot.Kind == ClipboardSnapshotKind.Text)
                {
                    var item = app.ClipboardRepository.AddText(snapshot.Text!, Environment.MachineName)!;
                    await app.SyncClient.PublishTextAsync(item).ConfigureAwait(false);
                }
                else
                {
                    var item = app.ClipboardRepository.AddImage(snapshot.Image!, Environment.MachineName);
                    await app.SyncClient.PublishImageAsync(item).ConfigureAwait(false);
                }
            });
            _localHistory.Refresh();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Manual clipboard synchronization failed: {0}", exception);
            app.SyncClient.ReportStatus("同步未完成：" + exception.Message);
        }
        finally { Interlocked.Exchange(ref _manualSyncRunning, 0); }
    }

    private async void ToggleQuickPasteMode()
    {
        if (_isQuickPasteMode)
        {
            await CloseQuickPasteModeAsync(restoreTargetFocus: true);
            return;
        }

        var targetWindowHandle = _pasteManager.GetForegroundWindowHandle();
        if (targetWindowHandle == new WindowInteropHelper(this).Handle) targetWindowHandle = nint.Zero;

        OnLocalClipboardChanged();

        SaveNormalWindowStateIfNeeded();
        _pasteTargetWindowHandle = targetWindowHandle;
        _isQuickPasteMode = true;

        Hide();
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Topmost = true;
        HistoryList.SelectedIndex = -1;
        HistorySearchBox.Clear();
        RefreshHistory();
        if (_historyItems.Count > 0) HistoryList.SelectedIndex = 0;
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
        if (e.Key == Key.Enter && HistoryList.SelectedItem is HistoryListEntry)
        {
            e.Handled = true;
            await PasteSelectedItemAsync();
            return;
        }

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
        if (e.ChangedButton != MouseButton.Left
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
        if (_isPasting || HistoryList.SelectedItem is not HistoryListEntry entry)
        {
            return;
        }

        var item = entry.Item;
        _isPasting = true;
        OnLocalClipboardChanged();

        var targetWindowHandle = _isQuickPasteMode ? _pasteTargetWindowHandle : nint.Zero;
        Hide();
        LeaveQuickPasteMode();
        try
        {
            var succeeded = await PasteSelectedItemAsync(item, targetWindowHandle, entry.ImageBytes);
            if (succeeded) _localHistory.MarkUsed(item.Id);
        }
        finally { _isPasting = false; }
    }

    private async Task<bool> PasteSelectedItemAsync(
        ClipBridge.Core.Models.ClipboardItem item,
        nint targetWindowHandle,
        byte[]? imageBytes = null)
    {
        try
        {
            switch (item.Type)
            {
                case ClipBridge.Core.Models.ClipboardItemType.Text when !string.IsNullOrEmpty(item.TextContent):
                    await _clipboardWriter.SetTextAsync(item.TextContent);
                    break;
                case ClipboardItemType.Image when imageBytes is not null || !string.IsNullOrEmpty(item.ImagePath):
                    if (imageBytes is not null) await _clipboardWriter.SetImageBytesAsync(imageBytes);
                    else await _clipboardWriter.SetImageAsync(item.ImagePath!);
                    break;
                case ClipboardItemType.Files when item.TextContent is not null:
                    await _clipboardWriter.SetFilesAsync(JsonSerializer.Deserialize<string[]>(item.TextContent) ?? []);
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

        await _pasteManager.PasteToAsync(targetWindowHandle);
        return true;
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

    public void StopLocalServices()
    {
        _localHistory.Changed -= OnLocalHistoryChanged;
        ((App)System.Windows.Application.Current).SyncClient.HistoryUpdated -= OnSynchronizedHistoryUpdated;
        ((App)System.Windows.Application.Current).SyncClient.RemoteClipboardReceived -= OnRemoteClipboardReceived;
        CancelPendingSynchronizedClipboardWrite();
        _clipboardMonitor.Dispose();
        _syncHotkey.Dispose();
        _clipboardWriter.Dispose();
        _synchronizedClipboardWriter.Dispose();
    }

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
