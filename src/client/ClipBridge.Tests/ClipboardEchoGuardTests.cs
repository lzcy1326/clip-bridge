using ClipBridge.Core.Models;
using ClipBridge.Core.Services;

namespace ClipBridge.Tests;

public sealed class ClipboardEchoGuardTests
{
    [Fact]
    public void RecentLocalTextReflectionIsSuppressedButOtherContentIsNot()
    {
        var clock = new TestClock();
        var guard = new ClipboardEchoGuard(clock);
        guard.RecordText("caf\u00e9");
        var repositoryHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("TEXTcaf\u00e9"))).ToLowerInvariant();
        var item = new ClipboardItem("id", "peer", ClipboardItemType.Text, "cafe\u0301", null, repositoryHash, 1, true, true);
        Assert.True(guard.IsRecentLocal(item));
        Assert.False(guard.IsRecentLocal(item with { ContentHash = "other" }));
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.False(guard.IsRecentLocal(item));
    }

    [Fact]
    public void RecentLocalImageReflectionIsSuppressed()
    {
        var png = new byte[] { 1, 2, 3 };
        var guard = new ClipboardEchoGuard();
        guard.RecordImage(png);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("IMAGE").Concat(png).ToArray())).ToLowerInvariant();
        var item = new ClipboardItem("id", "peer", ClipboardItemType.Image, null, "image.png", hash, 1, true, true);
        Assert.True(guard.IsRecentLocal(item));
        Assert.False(guard.IsRecentLocal(item with { Type = ClipboardItemType.Text }));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
