using System.Security.Cryptography;
using System.Text;
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
    public void AddImage_PersistsPngObjectAndShowsItInMixedHistory()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var imageBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };

        var added = repository.AddImage(imageBytes, "local");
        var reopenedRepository = new SqliteClipboardRepository(DatabasePath);
        var restored = reopenedRepository.FindById(added.Id);

        Assert.Equal(ClipboardItemType.Image, added.Type);
        Assert.Null(added.TextContent);
        Assert.Equal(64, added.ContentHash.Length);
        Assert.EndsWith(".png", added.ImagePath, StringComparison.Ordinal);
        Assert.True(File.Exists(added.ImagePath));
        Assert.Equal(imageBytes, File.ReadAllBytes(added.ImagePath!));
        Assert.NotNull(restored);
        Assert.Equal(added.ImagePath, restored.ImagePath);
        Assert.Single(reopenedRepository.GetRecentItems(10));
        Assert.Equal(added.Id, Assert.Single(reopenedRepository.GetRecentItems(10)).Id);
    }

    [Fact]
    public async Task AddImage_DeduplicatesSamePngAndMovesExistingItemToTop()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        var first = repository.AddImage(imageBytes, "device-a");
        await Task.Delay(5);
        repository.AddText("other text", "local");
        await Task.Delay(5);

        var recopied = repository.AddImage(imageBytes, "device-b");

        Assert.Equal(first.Id, recopied.Id);
        Assert.True(recopied.CreatedAtUnixMilliseconds > first.CreatedAtUnixMilliseconds);
        Assert.Equal("device-b", recopied.OriginDeviceId);
        Assert.Equal(2, repository.GetRecentItems(10).Count);
        Assert.Equal(recopied.Id, repository.GetRecentItems(10)[0].Id);
        Assert.Equal(first.ImagePath, recopied.ImagePath);
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "objects"), "*.png"));
    }

    [Fact]
    public void AddImage_AddsDifferentScreenshotContentAsSeparateHistoryItems()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var firstScreenshot = repository.AddImage(new byte[] { 1, 2, 3 }, "local");
        var secondScreenshot = repository.AddImage(new byte[] { 4, 5, 6 }, "local");

        Assert.NotEqual(firstScreenshot.Id, secondScreenshot.Id);
        Assert.NotEqual(firstScreenshot.ContentHash, secondScreenshot.ContentHash);
        Assert.Equal(2, repository.GetRecentItems(10).Count);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "objects"), "*.png").Length);
    }

    [Fact]
    public void CleanupHistory_RemovesExpiredSynchronizedItemsAndKeepsPendingItems()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var expired = repository.AddText("expired history", "local")!;
        Assert.True(repository.MarkSynced(expired.Id));
        var pending = repository.AddText("pending upload", "local")!;
        using (var connection = OpenDatabase())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE clipboard_items SET created_at = $created_at WHERE id = $id;";
            command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", expired.Id);
            command.ExecuteNonQuery();
        }

        var result = repository.CleanupHistory(retentionDays: 1, maximumContentBytes: 0);

        Assert.Equal(1, result.DeletedItems);
        Assert.Null(repository.FindById(expired.Id));
        Assert.NotNull(repository.FindById(pending.Id));
    }

    [Fact]
    public void CleanupHistory_EnforcesContentBudgetAndDeletesUnreferencedImageFiles()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var oldImage = repository.AddImage(new byte[] { 1, 2, 3, 4 }, "local");
        Assert.True(repository.MarkImageSynced(oldImage.Id));
        repository.AddText("newest", "local");
        Assert.True(repository.MarkSynced(Assert.Single(repository.GetRecentTextItems(1)).Id));

        var result = repository.CleanupHistory(retentionDays: 0, maximumContentBytes: Encoding.UTF8.GetByteCount("newest"));

        Assert.Equal(1, result.DeletedItems);
        Assert.Null(repository.FindById(oldImage.Id));
        Assert.False(File.Exists(oldImage.ImagePath));
        Assert.Equal(4, result.ReclaimedImageBytes);
        Assert.Equal("newest", Assert.Single(repository.GetRecentTextItems(1)).TextContent);
    }

    [Fact]
    public void CleanupHistory_WithZeroLimitsDoesNotDeleteHistory()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var item = repository.AddText("keep forever", "local");

        var result = repository.CleanupHistory(retentionDays: 0, maximumContentBytes: 0);

        Assert.Equal(0, result.DeletedItems);
        Assert.NotNull(repository.FindById(item!.Id));
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
    public void SearchTextItems_ReturnsCaseInsensitiveSubstringMatchesNewestFirst()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        repository.AddText("alpha older", "local");
        repository.AddText("unrelated", "local");
        repository.AddText("ALPHA newer", "local");

        var matches = repository.SearchTextItems("AlPhA", 10);

        Assert.Equal(new[] { "ALPHA newer", "alpha older" }, matches.Select(item => item.TextContent));
    }

    [Fact]
    public void SearchTextItems_TreatsLikeWildcardsAsLiteralText()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        repository.AddText("100%_complete", "local");
        repository.AddText("100xxcomplete", "local");

        var matches = repository.SearchTextItems("%_", 10);

        Assert.Equal("100%_complete", Assert.Single(matches).TextContent);
    }

    [Fact]
    public void SearchTextItems_BlankQueryReturnsRecentItemsAndHonorsLimit()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        repository.AddText("older", "local");
        repository.AddText("newer", "local");

        var matches = repository.SearchTextItems("  ", 1);

        Assert.Equal("newer", Assert.Single(matches).TextContent);
    }

    [Fact]
    public void SearchTextItems_RejectsNonPositiveLimit()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);

        Assert.Throws<ArgumentOutOfRangeException>(() => repository.SearchTextItems("text", 0));
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
    public void AddText_WhenMatchingRemoteTextIsCopiedLocallyCreatesFreshUploadId()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var remote = CreateRemoteTextItem("remote-id", "shared text", "HOST-PC", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        repository.UpsertRemoteText(remote, originatedLocally: false);

        var recopied = repository.AddText("shared text", "CLOUD-DEV")!;

        Assert.NotEqual("remote-id", recopied.Id);
        Assert.Equal("CLOUD-DEV", recopied.OriginDeviceId);
        Assert.False(recopied.Synced);
        Assert.False(recopied.Remote);
        Assert.Single(repository.GetRecentTextItems(10));
        Assert.Equal(recopied.Id, Assert.Single(repository.GetRecentTextItems(10)).Id);
    }

    [Fact]
    public void UpsertRemoteText_DeduplicatesContentAndKeepsNewestSource()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var baseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var first = CreateRemoteTextItem("remote-first", "shared text", "HOST-PC", baseTime);
        var newer = CreateRemoteTextItem("remote-second", "shared text", "CLOUD-DEV", baseTime + 1000);

        repository.UpsertRemoteText(first, originatedLocally: false);
        repository.UpsertRemoteText(newer, originatedLocally: false);

        var stored = Assert.Single(repository.GetRecentTextItems(10));
        Assert.Equal("CLOUD-DEV", stored.OriginDeviceId);
        Assert.Equal(baseTime + 1000, stored.CreatedAtUnixMilliseconds);
        Assert.True(stored.Synced);
        Assert.True(stored.Remote);
    }

    [Fact]
    public void UpsertRemoteTexts_BatchesItemsAndPreservesDeduplicationSemantics()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var baseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var first = CreateRemoteTextItem("remote-first", "shared text", "VM-PC", baseTime);
        var newer = CreateRemoteTextItem("remote-second", "shared text", "VM-PC", baseTime + 1000);
        var originatedLocally = CreateRemoteTextItem("local-origin", "text from this device", "HOST-PC", baseTime + 2000);

        repository.UpsertRemoteTexts([first, newer, originatedLocally], "HOST-PC");

        var stored = repository.GetRecentTextItems(10).ToDictionary(item => item.TextContent!);
        Assert.Equal(2, stored.Count);
        Assert.Equal("remote-first", stored["shared text"].Id);
        Assert.Equal("VM-PC", stored["shared text"].OriginDeviceId);
        Assert.Equal(baseTime + 1000, stored["shared text"].CreatedAtUnixMilliseconds);
        Assert.True(stored["shared text"].Remote);
        Assert.Equal("HOST-PC", stored["text from this device"].OriginDeviceId);
        Assert.False(stored["text from this device"].Remote);
    }

    [Fact]
    public void StartupCleanup_MigratesLegacyDuplicatesAndMarksSchemaVersion()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var initial = repository.AddText("legacy duplicate", "HOST-PC")!;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ux_clipboard_items_text_content_hash;
                PRAGMA user_version = 0;
                INSERT INTO clipboard_items
                    (id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote)
                VALUES
                    ('legacy-duplicate', 'VM-PC', 'TEXT', 'legacy duplicate', NULL, $hash, $created_at, 1, 1);
                """;
            command.Parameters.AddWithValue("$hash", initial.ContentHash);
            command.Parameters.AddWithValue("$created_at", initial.CreatedAtUnixMilliseconds + 1000);
            command.ExecuteNonQuery();
        }

        var migratedRepository = new SqliteClipboardRepository(DatabasePath);

        Assert.Equal(1, migratedRepository.GetTextCount());
        var restored = Assert.Single(migratedRepository.GetRecentTextItems(10));
        Assert.Equal("legacy-duplicate", restored.Id);
        Assert.True(restored.Synced);
        Assert.True(restored.Remote);
        using var versionConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        versionConnection.Open();
        using var versionCommand = versionConnection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(1, Convert.ToInt32(versionCommand.ExecuteScalar()));
    }

    [Fact]
    public void UpsertRemoteTextDoesNotReplaceNewerUnsyncedLocalCopy()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var local = repository.AddText("shared text", "HOST-PC")!;
        repository.UpsertRemoteText(local with
        {
            Id = "remote-copy",
            OriginDeviceId = "CLOUD-DEV",
            CreatedAtUnixMilliseconds = local.CreatedAtUnixMilliseconds - 1000,
            Synced = true,
            Remote = true
        }, originatedLocally: false);

        var stored = Assert.Single(repository.GetRecentTextItems(10));
        Assert.Equal(local.Id, stored.Id);
        Assert.Equal("HOST-PC", stored.OriginDeviceId);
        Assert.False(stored.Synced);
        Assert.False(stored.Remote);
    }

    [Fact]
    public void MarkSyncedMarksOnlyLocalTextItems()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var local = repository.AddText("local", "HOST-PC")!;
        var remote = CreateRemoteTextItem("remote-id", "remote", "CLOUD-DEV", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        repository.UpsertRemoteText(remote, originatedLocally: false);

        Assert.True(repository.MarkSynced(local.Id));
        Assert.False(repository.MarkSynced(remote.Id));
        Assert.True(repository.FindById(local.Id)!.Synced);
        Assert.True(repository.FindById(remote.Id)!.Remote);
    }

    [Fact]
    public void MarkUsed_MovesItemToTopAndPreservesCopyMetadata()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var usedItem = repository.AddText("older item", "device-a");
        Assert.NotNull(usedItem);
        repository.AddText("newer item", "device-b");

        var marked = repository.MarkUsed(usedItem.Id);
        var recent = repository.GetRecentTextItems(10);
        var restored = repository.FindById(usedItem.Id);

        Assert.True(marked);
        Assert.Equal(usedItem.Id, recent[0].Id);
        Assert.Equal(usedItem.CreatedAtUnixMilliseconds, restored!.CreatedAtUnixMilliseconds);
        Assert.Equal("device-a", restored.OriginDeviceId);
        Assert.NotNull(restored.LastUsedAtUnixMilliseconds);
        Assert.True(restored.LastUsedAtUnixMilliseconds > usedItem.CreatedAtUnixMilliseconds);
        Assert.False(restored.Synced);
        Assert.False(restored.Remote);
    }

    [Fact]
    public void MarkUsed_ReturnsFalseForMissingItem()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);

        Assert.False(repository.MarkUsed("missing"));
    }

    [Fact]
    public void MarkUsed_MovesImageToTopAndPreservesCopyMetadata()
    {
        var repository = new SqliteClipboardRepository(DatabasePath);
        var image = repository.AddImage(new byte[] { 1, 2, 3, 4 }, "device-a");
        repository.AddText("newer text", "device-b");

        Assert.True(repository.MarkUsed(image.Id));

        var recent = repository.GetRecentItems(10);
        var restored = repository.FindById(image.Id);
        Assert.Equal(image.Id, recent[0].Id);
        Assert.Equal(image.CreatedAtUnixMilliseconds, restored!.CreatedAtUnixMilliseconds);
        Assert.Equal(image.ImagePath, restored.ImagePath);
        Assert.NotNull(restored.LastUsedAtUnixMilliseconds);
    }

    [Fact]
    public void Constructor_AddsLastUsedColumnToExistingDatabase()
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
                INSERT INTO clipboard_items VALUES ('existing', 'local', 'TEXT', 'existing text', NULL, 'hash', 123, 0, 0);
                """;
            command.ExecuteNonQuery();
        }

        var repository = new SqliteClipboardRepository(DatabasePath);
        var existing = repository.FindById("existing");

        Assert.NotNull(existing);
        Assert.Null(existing.LastUsedAtUnixMilliseconds);
        Assert.True(repository.MarkUsed(existing.Id));
        Assert.NotNull(repository.FindById(existing.Id)!.LastUsedAtUnixMilliseconds);
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
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ClipboardItem CreateRemoteTextItem(string id, string text, string originDeviceId, long createdAt)
    {
        var normalized = text.Normalize(NormalizationForm.FormC);
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("TEXT" + normalized))).ToLowerInvariant();
        return new ClipboardItem(id, originDeviceId, ClipboardItemType.Text, text, null, contentHash, createdAt, true, true);
    }

    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        connection.Open();
        return connection;
    }
}
