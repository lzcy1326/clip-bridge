using ClipBridge.App;

namespace ClipBridge.Tests;

public class RemoteClipboardWritePriorityTests
{
    [Fact]
    public void OldRemoteItemCannotReplaceNewLocalCopyWhenServerClockIsAhead()
    {
        const long localCopy = 1_000_000;
        const long serverAhead = 60_000;
        Assert.False(RemoteClipboardWritePriority.IsNewerThanLocalCopy(
            localCopy + 30_000, localCopy, serverAhead, 1_500));
        Assert.True(RemoteClipboardWritePriority.IsNewerThanLocalCopy(
            localCopy + serverAhead + 3_000, localCopy, serverAhead, 1_500));
    }

    [Fact]
    public void NearSimultaneousRemoteCopyDefersToLocalClipboard()
    {
        Assert.False(RemoteClipboardWritePriority.IsNewerThanLocalCopy(1_000_500, 1_000_000, 0, 1_500));
    }
}
