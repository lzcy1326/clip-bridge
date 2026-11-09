using System.Windows;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using ClipBridge.Core.Diagnostics;
using ClipBridge.Core.Interfaces;
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

    public IClipboardRepository ClipboardRepository { get; private set; } = null!;
    public ClipBridgeSyncClient SyncClient { get; private set; } = null!;
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
        HistoryRetentionSettingsPath = Path.Combine(appDataDirectory, "history-retention.json");
        _historyCleanupService = new LocalHistoryCleanupService(localRepository, HistoryRetentionSettingsPath);
        StartupPerformanceLog.Write("app.database-ready", startupTimer.Elapsed);
        startupTimer.Restart();
        SyncClient = new ClipBridgeSyncClient(
            ClipboardRepository,
            Path.Combine(appDataDirectory, "sync-profiles.json"));
        LocalDeviceCode = SyncClient.GetLocalDeviceCodeAsync().GetAwaiter().GetResult();
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
        _ = SyncClient.RestoreDeviceSessionAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _historyCleanupService?.Dispose();
        _trayIconService?.Dispose();
        SyncClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
                accept => SyncClient.DecidePairingRequestAsync(request.Id, accept, peerDeviceCode: request.RequesterCode),
                () => SyncClient.DismissPairingPrompt(request.Id));
            window.Closed += (_, _) => _pairingRequestWindows.Remove(request.Id);
            _pairingRequestWindows.Add(request.Id, window);
            window.Show();
            window.Activate();
        }));
    }

    public void ExitApplication()
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.AllowApplicationExit();
        }

        Shutdown();
    }
}
