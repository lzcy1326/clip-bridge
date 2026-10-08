using System.Diagnostics;
using ClipBridge.Storage.Repositories;

namespace ClipBridge.App;

internal sealed class LocalHistoryCleanupService : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);
    private readonly SqliteClipboardRepository _repository;
    private readonly string _settingsPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    public LocalHistoryCleanupService(SqliteClipboardRepository repository, string settingsPath)
    {
        _repository = repository;
        _settingsPath = settingsPath;
        _worker = RunAsync(_stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(InitialDelay, cancellationToken);
        using var timer = new PeriodicTimer(CleanupInterval);
        do
        {
            try
            {
                var settings = HistoryRetentionSettings.Load(_settingsPath);
                if (settings.RetentionDays > 0 || settings.MaximumItems > 0 || settings.MaximumContentBytes > 0)
                {
                    var result = await Task.Run(
                        () => _repository.CleanupHistoryWithLimits(
                            settings.RetentionDays,
                            settings.MaximumItems,
                            settings.MaximumContentBytes),
                        cancellationToken);
                    if (result.DeletedItems > 0)
                    {
                        Trace.TraceInformation(
                            "Clipboard history cleanup removed {0} items and {1} image bytes.",
                            result.DeletedItems,
                            result.ReclaimedImageBytes);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceError("Clipboard history cleanup failed: {0}", exception);
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
