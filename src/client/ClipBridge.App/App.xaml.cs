using System.Windows;
using System.IO;
using ClipBridge.Core.Interfaces;
using ClipBridge.Storage.Repositories;

namespace ClipBridge.App;

public partial class App : System.Windows.Application
{
    private TrayIconService? _trayIconService;

    public IClipboardRepository ClipboardRepository { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipBridge");
        ClipboardRepository = new SqliteClipboardRepository(
            Path.Combine(appDataDirectory, "clipbridge.db"));

        base.OnStartup(e);
        _trayIconService = new TrayIconService();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        base.OnExit(e);
    }
}
