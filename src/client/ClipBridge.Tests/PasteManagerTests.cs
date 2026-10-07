using ClipBridge.Windows;

namespace ClipBridge.Tests;

public sealed class PasteManagerTests
{
    [Fact]
    public async Task RestoreForegroundWindowForPasteAsync_WhenTargetAlreadyHasFocus_DoesNotRequestActivation()
    {
        var activationRequests = 0;
        var manager = CreateManager(
            foregroundWindow: 42,
            isWindow: _ => true,
            setForegroundWindow: _ =>
            {
                activationRequests++;
                return false;
            });

        var restored = await manager.RestoreForegroundWindowForPasteAsync(42);

        Assert.True(restored);
        Assert.Equal(0, activationRequests);
    }

    [Fact]
    public async Task RestoreForegroundWindowForPasteAsync_RetriesUntilTargetReceivesFocus()
    {
        nint foregroundWindow = 7;
        var activationRequests = 0;
        var manager = new PasteManager(
            () => foregroundWindow,
            _ => true,
            targetWindow =>
            {
                activationRequests++;
                if (activationRequests == 3)
                {
                    foregroundWindow = targetWindow;
                }

                return activationRequests == 3;
            },
            (_, _) => Task.CompletedTask);

        var restored = await manager.RestoreForegroundWindowForPasteAsync(42);

        Assert.True(restored);
        Assert.Equal(3, activationRequests);
        Assert.Equal((nint)42, foregroundWindow);
    }

    [Fact]
    public async Task PasteFocus_RestoresOriginalInputControlBeforeAllowingPaste()
    {
        nint foregroundWindow = 7;
        nint focusedWindow = 77;
        var elapsedWaits = 0;
        var manager = new PasteManager(
            () => foregroundWindow,
            _ => true,
            targetWindow =>
            {
                foregroundWindow = targetWindow;
                return true;
            },
            (_, _) =>
            {
                elapsedWaits++;
                if (elapsedWaits == 2)
                {
                    focusedWindow = 77;
                }

                return Task.CompletedTask;
            },
            _ => focusedWindow);

        var targetWindow = manager.GetForegroundWindowHandle();
        foregroundWindow = 99;
        focusedWindow = nint.Zero;

        var restored = await manager.RestoreForegroundWindowForPasteAsync(targetWindow);

        Assert.True(restored);
        Assert.Equal(2, elapsedWaits);
        Assert.Equal((nint)77, focusedWindow);
    }

    [Fact]
    public async Task RestoreForegroundWindowForPasteAsync_WhenTargetIsInvalid_DoesNotRequestActivation()
    {
        var activationRequests = 0;
        var manager = CreateManager(
            foregroundWindow: 7,
            isWindow: _ => false,
            setForegroundWindow: _ =>
            {
                activationRequests++;
                return true;
            });

        var restored = await manager.RestoreForegroundWindowForPasteAsync(42);

        Assert.False(restored);
        Assert.Equal(0, activationRequests);
    }

    private static PasteManager CreateManager(
        nint foregroundWindow,
        Func<nint, bool> isWindow,
        Func<nint, bool> setForegroundWindow) => new(
        () => foregroundWindow,
        isWindow,
        setForegroundWindow,
        (_, _) => Task.CompletedTask);
}
