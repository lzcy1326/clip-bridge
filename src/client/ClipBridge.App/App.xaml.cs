using System.Windows;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using ClipBridge.Core.Diagnostics;
using ClipBridge.Core.Interfaces;
using ClipBridge.Core.Services;
using ClipBridge.Storage.Repositories;
using ClipBridge.Sync;

namespace ClipBridge.App;

public partial class App : System.Windows.Application
{
    private readonly Dictionary<string, PairingRequestWindow> _pairingRequestWindows = new(StringComparer.Ordinal);
    private TrayIconService? _trayIconService;
    private SyncConnectionWindow? _syncConnectionWindow;
    private HistoryRetentionWindow? _historyRetentionWindow;
    private LocalHistoryCleanupService? _historyCleanupService;
    private readonly CancellationTokenSource _networkStop = new();
    private bool _isExiting;

    public IClipboardRepository ClipboardRepository { get; private set; } = null!;
    public ClipBridgeSyncClient SyncClient { get; private set; } = null!;
    public LocalClipboardHistory LocalHistory { get; private set; } = null!;
    public string LocalDeviceCode { get; internal set; } = string.Empty;
    public string HistoryRetentionSettingsPath { get; private set; } = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        var appStartupTimer = Stopwatch.StartNew();
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipBridge");
        StartupPerformanceLog.Configure(Path.Combine(appDataDirectory, "startup-performance.log"));
        var startupTimer = Stopwatch.StartNew();
        var localRepository = new SqliteClipboardRepository(Path.Combine(appDataDirectory, "clipbridge.db"));
        ClipboardRepository = localRepository;
        LocalHistory = new LocalClipboardHistory(localRepository);
        LocalHistory.Failed += exception => Trace.TraceError("Local clipboard history could not be saved: {0}", exception);
        HistoryRetentionSettingsPath = Path.Combine(appDataDirectory, "history-retention.json");
        _historyCleanupService = new LocalHistoryCleanupService(localRepository, HistoryRetentionSettingsPath);
        StartupPerformanceLog.Write("app.database-ready", startupTimer.Elapsed);
        startupTimer.Restart();
        SyncClient = new ClipBridgeSyncClient(
            ClipboardRepository,
            Path.Combine(appDataDirectory, "sync-profiles.json"));
        StartupPerformanceLog.Write("app.local-device-ready", startupTimer.Elapsed);
        SyncClient.PairingRequestReceived += OnPairingRequestReceived;

        base.OnStartup(e);
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        if (e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            // Create the HWND so the clipboard listener and global hotkey can run,
            // without showing the window or leaving it with minimized restore bounds.
            _ = new System.Windows.Interop.WindowInteropHelper(mainWindow).EnsureHandle();
        }
        else
        {
            mainWindow.Show();
        }

        _trayIconService = new TrayIconService();
        StartupPerformanceLog.Write("app.startup-ready", appStartupTimer.Elapsed);
        _ = Task.Run(InitializeSyncAsync);
    }

    private async Task InitializeSyncAsync()
    {
        try
        {
            var code = await SyncClient.GetLocalDeviceCodeAsync(_networkStop.Token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                LocalDeviceCode = code;
                _syncConnectionWindow?.SetLocalDeviceCode(code);
            });
            await SyncClient.RestoreDeviceSessionAsync(_networkStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_networkStop.IsCancellationRequested) { }
        catch (Exception exception) { Trace.TraceError("Background synchronization startup failed: {0}", exception); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.StopLocalServices();
        _networkStop.Cancel();
        _trayIconService?.Dispose();
        // Neither an unavailable server nor a locked database may hold the UI at exit.
        try
        {
            Task.WhenAll(LocalHistory.DisposeAsync().AsTask(),
                Task.Run(() => _historyCleanupService?.Dispose()),
                Task.Run(async () => await SyncClient.DisposeAsync())).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) { Trace.TraceWarning("Background shutdown failed: {0}", exception); }
        base.OnExit(e);
    }

    public void ShowSyncConnectionWindow()
    {
        if (_syncConnectionWindow is { IsVisible: true })
        {
            _syncConnectionWindow.Activate();
            return;
        }

        var window = new SyncConnectionWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.SetLocalDeviceCode(LocalDeviceCode);
        window.SetSavedConnection(SyncClient.ConnectedServerAddress, SyncClient.PeerDeviceCode);
        _syncConnectionWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_syncConnectionWindow, window))
            {
                _syncConnectionWindow = null;
            }
        };
        window.Show();
        window.Activate();
    }

    public void ShowHistoryRetentionWindow()
    {
        if (_historyRetentionWindow is { IsVisible: true })
        {
            _historyRetentionWindow.Activate();
            return;
        }

        var window = new HistoryRetentionWindow(HistoryRetentionSettingsPath);
        _historyRetentionWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_historyRetentionWindow, window))
            {
                _historyRetentionWindow = null;
            }
        };
        window.Show();
        window.Activate();
    }

    private void OnPairingRequestReceived(ClipBridge.Sync.PendingPairingInfo request)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (_pairingRequestWindows.TryGetValue(request.Id, out var existingWindow))
            {
                existingWindow.Activate();
                return;
            }

            var window = new PairingRequestWindow(
                request,
                accept => Task.Run(() => SyncClient.DecidePairingRequestAsync(request.Id, accept, peerDeviceCode: request.RequesterCode)),
                () => SyncClient.DismissPairingPrompt(request.Id));
            window.Closed += (_, _) => _pairingRequestWindows.Remove(request.Id);
            _pairingRequestWindows.Add(request.Id, window);
            window.Show();
            window.Activate();
        }));
    }

    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.AllowApplicationExit();
        }

        // 兜底：Shutdown 若因异常路径未能结束进程，至少不要留下幽灵菜单和后台实例。
        // 正常退出会在计时器触发前结束进程。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            Environment.Exit(0);
        });

        Shutdown();
    }
}
