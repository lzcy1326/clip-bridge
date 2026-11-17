using System.Windows.Threading;

namespace ClipBridge.Windows;

internal sealed class StaTaskRunner : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public StaTaskRunner(string name)
    {
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ready.TrySetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var dispatcher = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ = _ready.Task.ContinueWith(task => task.Result.BeginInvokeShutdown(DispatcherPriority.Send), TaskScheduler.Default);
    }
}
