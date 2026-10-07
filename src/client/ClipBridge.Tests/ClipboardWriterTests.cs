using System.Runtime.InteropServices;
using ClipBridge.Windows;

namespace ClipBridge.Tests;

public sealed class ClipboardWriterTests
{
    [Fact]
    public async Task SetTextRetriesTransientClipboardContention()
    {
        var attempts = 0;
        var writer = new ClipboardWriter(setText: _ =>
        {
            if (Interlocked.Increment(ref attempts) < 3)
            {
                throw new ExternalException("Clipboard is temporarily busy.");
            }
        });

        await writer.SetTextAsync("clipboard retry test");

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task SetTextStopsAfterFiveAttemptsWhenClipboardRemainsBusy()
    {
        var attempts = 0;
        var writer = new ClipboardWriter(setText: _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new ExternalException("Clipboard is still busy.");
        });

        await Assert.ThrowsAsync<ExternalException>(() => writer.SetTextAsync("clipboard retry test"));

        Assert.Equal(5, attempts);
    }
}
