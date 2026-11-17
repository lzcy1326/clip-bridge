using System.Diagnostics;
using ClipBridge.Windows;

namespace ClipBridge.Tests;

public sealed class ClipboardIsolationTests
{
    [Fact]
    public async Task StaleSynchronizedTextDoesNotOverwriteChangedClipboard()
    {
        var called = false;
        using var writer = new ClipboardWriter(setText: _ => called = true);
        var staleSequence = ClipboardWriter.CurrentSequenceNumber ^ 1u;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.SetTextAsync("old remote", expectedSequence: staleSequence));
        Assert.False(called);
    }

    [Fact]
    public async Task ClipboardWriterReturnsImmediatelyAndRunsOnAnotherStaThread()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerThread = Environment.CurrentManagedThreadId;
        var workerThread = 0;
        var apartment = ApartmentState.Unknown;
        using var writer = new ClipboardWriter(setText: _ =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            apartment = Thread.CurrentThread.GetApartmentState();
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        });
        var timer = Stopwatch.StartNew();
        var write = writer.SetTextAsync("local write");
        try
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(write.IsCompleted);
            Assert.NotEqual(callerThread, workerThread);
            Assert.Equal(ApartmentState.STA, apartment);
        }
        finally { release.Set(); }
        await write;
    }

    [Fact]
    public async Task BlockedReaderCannotBlockTheSeparateClipboardWriter()
    {
        using var release = new ManualResetEventSlim();
        using var reader = new StaTaskRunner("blocked reader test");
        using var writer = new ClipboardWriter(setText: _ => { });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = reader.RunAsync(() =>
        {
            entered.TrySetResult();
            return release.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await writer.SetTextAsync("independent local copy").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(read.IsCompleted);
        }
        finally { release.Set(); }
        await read;
    }
}
