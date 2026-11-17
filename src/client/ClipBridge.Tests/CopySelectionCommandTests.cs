using ClipBridge.Windows;

namespace ClipBridge.Tests;

public sealed class CopySelectionCommandTests
{
    [Fact]
    public async Task WaitsForShortcutReleaseThenReturnsNewClipboardSequence()
    {
        var heldChecks = 0;
        var sequence = 10u;
        var sends = 0;
        var command = new CopySelectionCommand(_ => ++heldChecks <= 2, () =>
        {
            Assert.True(heldChecks > 2);
            sends++;
            sequence = 11;
            return true;
        }, () => sequence, (_, _) => Task.CompletedTask);

        var result = await command.CopyAsync(0x43);
        Assert.Equal(10u, result.PreviousSequence);
        Assert.Equal(11u, result.CopiedSequence);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task NoCopyLeavesSequenceUnchangedForScreenshotFallback()
    {
        var command = new CopySelectionCommand(_ => false, () => true, () => 42, (_, _) => Task.CompletedTask);
        var result = await command.CopyAsync(0x43);
        Assert.Equal(42u, result.PreviousSequence);
        Assert.Null(result.CopiedSequence);
    }

    [Fact]
    public async Task FailedInputDoesNotPretendCopySucceeded()
    {
        var command = new CopySelectionCommand(_ => false, () => false, () => 42, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.CopyAsync(0x43));
    }

    [Fact]
    public async Task FocusChangeCancelsCopyRatherThanCopyingFromAnotherWindow()
    {
        var calls = 0;
        var sent = false;
        var command = new CopySelectionCommand(_ => false, () => { sent = true; return true; }, () => 42,
            (_, _) => Task.CompletedTask, () => new nint(++calls));
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.CopyAsync(0x43));
        Assert.False(sent);
    }

    [Fact]
    public void ScreenshotFallbackAndClipboardRacesAreDistinguished()
    {
        var unchanged = new CopyAttempt(42, null);
        Assert.Equal(CopySnapshotResult.NoNewCopy, CopySelectionCommand.Evaluate(unchanged, new(ClipboardSnapshotKind.Image, 42, Image: [1])));
        Assert.Equal(CopySnapshotResult.NoNewCopy, CopySelectionCommand.Evaluate(unchanged, new(ClipboardSnapshotKind.Text, 42, Text: "old text")));
        Assert.Equal(CopySnapshotResult.Changed, CopySelectionCommand.Evaluate(unchanged, new(ClipboardSnapshotKind.Image, 43, Image: [1])));
        var copied = new CopyAttempt(42, 43);
        Assert.Equal(CopySnapshotResult.Ready, CopySelectionCommand.Evaluate(copied, new(ClipboardSnapshotKind.Text, 43, Text: "selected")));
        Assert.Equal(CopySnapshotResult.Files, CopySelectionCommand.Evaluate(copied, new(ClipboardSnapshotKind.Files, 43)));
        Assert.Equal(CopySnapshotResult.Changed, CopySelectionCommand.Evaluate(copied, new(ClipboardSnapshotKind.Text, 44, Text: "other")));
    }
}
