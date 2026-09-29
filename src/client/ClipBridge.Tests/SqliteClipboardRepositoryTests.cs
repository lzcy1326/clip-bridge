using ClipBridge.Core.Models;
using ClipBridge.Storage.Repositories;
using Microsoft.Data.Sqlite;

namespace ClipBridge.Tests;

public sealed class SqliteClipboardRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ClipBridge.Tests",
        Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "clipbridge.db");

    [Fact]
    public void AddText_PersistsTextWhenRepositoryIsReopened()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var added = repository.AddText("CLIPBRIDGE-MVP03-text", "local");

        var reopenedRepository = new SqliteClipboardRepository(DatabasePath);
        var restored = reopenedRepository.FindById(added.Id);

        Assert.Equal(1, reopenedRepository.GetTextCount());
        Assert.NotNull(restored);
        Assert.Equal(added.Id, restored.Id);
        Assert.Equal("local", restored.OriginDeviceId);
        Assert.Equal(ClipboardItemType.Text, restored.Type);
        Assert.Equal("CLIPBRIDGE-MVP03-text", restored.TextContent);
        Assert.Null(restored.ImagePath);
        Assert.Equal(added.ContentHash, restored.ContentHash);
        Assert.Equal(64, restored.ContentHash.Length);
        Assert.False(restored.Synced);
        Assert.False(restored.Remote);
    }

    [Fact]
    public void GetRecentTextItems_ReturnsNewestFirstAndHonorsLimit()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        repository.AddText("first", "local");
        repository.AddText("second", "local");
        repository.AddText("third", "local");

        var recent = repository.GetRecentTextItems(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal(new[] { "third", "second" }, recent.Select(item => item.TextContent));
    }

    [Fact]
    public void GetRecentTextItems_RejectsNonPositiveLimit()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);

        Assert.Throws<ArgumentOutOfRangeException>(() => repository.GetRecentTextItems(0));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var databasePath = DatabasePath;
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory);
        }
    }
}
