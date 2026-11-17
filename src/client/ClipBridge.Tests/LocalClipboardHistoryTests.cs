using System.Diagnostics;
using System.Reflection;
using ClipBridge.Core.Interfaces;
using ClipBridge.Core.Models;
using ClipBridge.Core.Services;
using ClipBridge.Storage.Repositories;

namespace ClipBridge.Tests;

public sealed class LocalClipboardHistoryTests
{
    [Fact]
    public async Task LocalOnlyCopiesRemainIneligibleWhenDatabaseSaveIsDelayed()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipBridge.Tests", Guid.NewGuid().ToString("N"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var actual = new SqliteClipboardRepository(Path.Combine(root, "history.db"));
            var proxy = DispatchProxy.Create<IClipboardRepository, RepositoryProbe>();
            ((RepositoryProbe)proxy).Handler = (method, args) =>
            {
                if (method.Name == nameof(IClipboardRepository.GetRecentItems))
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                return method.Invoke(actual, args);
            };
            var history = new LocalClipboardHistory(proxy);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                history.CaptureText("private copy", "local", allowAutomaticUpload: false);
                history.CaptureImage([1, 2, 3], "local", allowAutomaticUpload: false);
                // Persistence happens only after the application could have switched modes.
                release.Set();
                await history.DisposeAsync();
                Assert.Empty(actual.GetPendingTexts(10));
                Assert.Empty(actual.GetPendingImages(10));
                Assert.Equal(2, actual.GetRecentItems(10).Count);
                actual.AddText("private copy", "local", allowAutomaticUpload: true);
                actual.AddImage(new byte[] { 1, 2, 3 }, "local", allowAutomaticUpload: true);
                Assert.Single(actual.GetPendingTexts(10));
                Assert.Single(actual.GetPendingImages(10));
                actual.AddText("private copy", "local", allowAutomaticUpload: false);
                actual.AddImage(new byte[] { 1, 2, 3 }, "local", allowAutomaticUpload: false);
                Assert.Empty(actual.GetPendingTexts(10));
                Assert.Empty(actual.GetPendingImages(10));
            }
            finally { release.Set(); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BlockedDatabaseDoesNotDelayLocalHistoryOrLoseCopies()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = DispatchProxy.Create<IClipboardRepository, RepositoryProbe>();
        ((RepositoryProbe)repository).Handler = (method, args) =>
        {
            if (method.Name == nameof(IClipboardRepository.GetRecentItems))
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return Array.Empty<ClipboardItem>();
            }
            if (method.Name == nameof(IClipboardRepository.AddText))
                return Item(ClipboardItemType.Text, (string)args![0]!);
            if (method.Name == nameof(IClipboardRepository.AddFiles))
                return Item(ClipboardItemType.Files, System.Text.Json.JsonSerializer.Serialize(args![0]));
            if (method.Name == nameof(IClipboardRepository.AddImage))
                return Item(ClipboardItemType.Image, null);
            return true;
        };
        var history = new LocalClipboardHistory(repository);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var timer = Stopwatch.StartNew();
            history.CaptureText("offline text", "local");
            history.CaptureImage([1, 2, 3], "local");
            history.CaptureFiles([@"C:\offline.txt"], "local");
            var snapshot = history.Snapshot();
            Assert.Equal(3, snapshot.Count);
            Assert.Equal(new byte[] { 1, 2, 3 }, snapshot.Single(x => x.Item.Type == ClipboardItemType.Image).ImageBytes);
            Assert.Single(history.Snapshot("offline.txt"));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"Memory-only capture took {timer.Elapsed}.");
            Assert.False(history.IsLoaded);
        }
        finally
        {
            release.Set();
            await history.DisposeAsync();
        }
        Assert.Equal(3, history.Snapshot().Count);
    }

    [Fact]
    public async Task FailedSaveKeepsItemAvailableAndDoesNotStopFollowingCaptures()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = DispatchProxy.Create<IClipboardRepository, RepositoryProbe>();
        ((RepositoryProbe)repository).Handler = (method, args) => method.Name switch
        {
            nameof(IClipboardRepository.GetRecentItems) => Array.Empty<ClipboardItem>(),
            nameof(IClipboardRepository.AddText) when (string)args![0]! == "keep in memory" => throw new IOException("disk unavailable"),
            nameof(IClipboardRepository.AddText) => Item(ClipboardItemType.Text, (string)args![0]!),
            _ => true
        };
        var history = new LocalClipboardHistory(repository);
        history.Failed += _ => failed.TrySetResult();
        history.CaptureText("keep in memory", "local");
        history.CaptureText("next copy", "local");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await history.DisposeAsync();
        Assert.Contains(history.Snapshot(), entry => entry.Item.TextContent == "keep in memory");
        Assert.Contains(history.Snapshot(), entry => entry.Item.TextContent == "next copy");
    }

    [Fact]
    public async Task FilesPersistLocallyAndAreExcludedFromTextAndImageUploadQueues()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipBridge.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new SqliteClipboardRepository(Path.Combine(root, "history.db"));
            var history = new LocalClipboardHistory(repository);
            history.CaptureFiles([@"C:\folder\中文.txt", @"C:\folder\image.png"], "local");
            history.CaptureText("pending upload", "local");
            await history.DisposeAsync();
            var reopened = new SqliteClipboardRepository(Path.Combine(root, "history.db"));
            var files = Assert.Single(reopened.GetRecentItems(10), item => item.Type == ClipboardItemType.Files);
            Assert.Equal(new[] { @"C:\folder\中文.txt", @"C:\folder\image.png" }, System.Text.Json.JsonSerializer.Deserialize<string[]>(files.TextContent!));
            Assert.Equal("pending upload", Assert.Single(reopened.GetPendingTexts(10)).TextContent);
            Assert.Empty(reopened.GetPendingImages(10));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ClipboardItem Item(ClipboardItemType type, string? text) =>
        new(Guid.NewGuid().ToString("N"), "local", type, text, null, Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, false);

    public class RepositoryProbe : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
