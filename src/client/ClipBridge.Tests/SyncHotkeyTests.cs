using System.Windows;
using ClipBridge.Windows;

namespace ClipBridge.Tests;

public sealed class SyncHotkeyTests
{
    [Fact]
    public async Task ManualShortcutCannotClaimOrdinaryControlC()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window();
                using var hotkey = new SyncHotkey(window, () => { });
                Assert.False(hotkey.Configure(true, 2, 0x43, out _));
                Assert.True(hotkey.Configure(true, 6, 0x43, out _));
                Assert.True(hotkey.Configure(false, 6, 0x43, out _));
                completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
