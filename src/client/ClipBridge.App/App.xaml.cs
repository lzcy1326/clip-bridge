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

    public IClipboardRepository ClipboardRepository { get; private set; } = null!;
    public ClipBridgeSyncClient SyncClient { get; private set; } = null!;
    public string LocalDeviceCode { get; internal set; } = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        var appStartupTimer = Stopwatch.StartNew();
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipBridge");
        StartupPerformanceLog.Configure(Path.Combine(appDataDirectory, "startup-performance.log"));
        var startupTimer = Stopwatch.StartNew();
        ClipboardRepository = new SqliteClipboardRepository(
            Path.Combine(appDataDirectory, "clipbridge.db"));
        StartupPerformanceLog.Write("app.database-ready", startupTimer.Elapsed);
        startupTimer.Restart();
        SyncClient = new ClipBridgeSyncClient(
            ClipboardRepository,
            Path.Combine(appDataDirectory, "sync-profiles.json"));
        LocalDeviceCode = SyncClient.GetLocalDeviceCodeAsync().GetAwaiter().GetResult();
        StartupPerformanceLog.Write("app.local-device-ready", startupTimer.Elapsed);
        SyncClient.PairingRequestReceived += OnPairingRequestReceived;

        base.OnStartup(e);
        _trayIconService = new TrayIconService();
        StartupPerformanceLog.Write("app.startup-ready", appStartupTimer.Elapsed);
        _ = SyncClient.RestoreDeviceSessionAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
