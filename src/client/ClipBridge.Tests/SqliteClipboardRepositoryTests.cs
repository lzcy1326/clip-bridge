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
        Assert.NotNull(added);

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

    [Fact]
    public void AddText_IgnoresBlankText()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);

        var added = repository.AddText(" \t\r\n", "local");

        Assert.Null(added);
        Assert.Equal(0, repository.GetTextCount());
        Assert.Empty(repository.GetRecentTextItems(10));
    }

    [Fact]
    public async Task AddText_UpdatesExistingRecordWhenTextIsCopiedAgain()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var first = repository.AddText("same text", "device-a");
        Assert.NotNull(first);
        await Task.Delay(5);
        repository.AddText("other text", "local");
        await Task.Delay(5);

        var recopied = repository.AddText("same text", "device-b");

        Assert.NotNull(recopied);
        Assert.Equal(first.Id, recopied.Id);
        Assert.True(recopied.CreatedAtUnixMilliseconds > first.CreatedAtUnixMilliseconds);
        Assert.Equal("device-b", recopied.OriginDeviceId);
        Assert.Equal(2, repository.GetTextCount());
        var recentItems = repository.GetRecentTextItems(10);
        Assert.Equal(2, recentItems.Count);
        Assert.Equal(recopied.Id, recentItems[0].Id);
        Assert.Equal(recopied.CreatedAtUnixMilliseconds, recentItems[0].CreatedAtUnixMilliseconds);
        Assert.Equal("other text", recentItems[1].TextContent);
    }

    [Fact]
    public void Constructor_RemovesExistingBlankAndDuplicateRecordsKeepingNewestCopy()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE clipboard_items (
                    id TEXT PRIMARY KEY,
                    origin_device_id TEXT NOT NULL,
                    type TEXT NOT NULL,
                    text_content TEXT,
                    image_path TEXT,
                    content_hash TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    synced INTEGER NOT NULL DEFAULT 0,
                    remote INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO clipboard_items VALUES ('older', 'local', 'TEXT', 'same', NULL, 'old-hash', 1, 0, 0);
                INSERT INTO clipboard_items VALUES ('newer', 'local', 'TEXT', 'same', NULL, 'new-hash', 2, 0, 0);
                INSERT INTO clipboard_items VALUES ('blank', 'local', 'TEXT', '  ' || char(9) || char(10), NULL, 'blank-hash', 3, 0, 0);
                """;
            command.ExecuteNonQuery();
        }

        var repository = new SqliteClipboardRepository(DatabasePath);

        var recent = Assert.Single(repository.GetRecentTextItems(10));
        Assert.Equal("newer", recent.Id);
        Assert.Equal("same", recent.TextContent);
        Assert.Equal(1, repository.GetTextCount());
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
