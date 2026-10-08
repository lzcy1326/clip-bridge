using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using ClipBridge.Core.Diagnostics;
using ClipBridge.Core.Interfaces;
using ClipBridge.Core.Models;
using Microsoft.Data.Sqlite;

namespace ClipBridge.Storage.Repositories;

public sealed class SqliteClipboardRepository : IClipboardRepository
{
    private const string TextTypeName = "TEXT";
    private const string ImageTypeName = "IMAGE";
    private readonly string _connectionString;
    private readonly string _objectsDirectory;

    public SqliteClipboardRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var dataDirectory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dataDirectory);
        _objectsDirectory = Path.Combine(dataDirectory, "objects");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeDatabase();
    }

    public ClipboardItem? AddText(string text, string originDeviceId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceId);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalizedText = text.Normalize(NormalizationForm.FormC);
        var contentHash = ComputeContentHash(TextTypeName, Encoding.UTF8.GetBytes(normalizedText));
        var item = new ClipboardItem(
            Guid.NewGuid().ToString("N"),
            originDeviceId,
            ClipboardItemType.Text,
            text,
            null,
            contentHash,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            false,
            false);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var findCommand = connection.CreateCommand())
        {
            findCommand.Transaction = transaction;
            findCommand.CommandText = """
                SELECT id, created_at, remote
                FROM clipboard_items
                WHERE type = $type AND content_hash = $content_hash
                LIMIT 1;
                """;
            findCommand.Parameters.AddWithValue("$type", TextTypeName);
            findCommand.Parameters.AddWithValue("$content_hash", item.ContentHash);

            using var existingReader = findCommand.ExecuteReader();
            if (existingReader.Read())
            {
                var existingId = existingReader.GetString(0);
                var existingCreatedAt = existingReader.GetInt64(1);
                var existingIsRemote = existingReader.GetInt32(2) != 0;
                var updatedItem = item with
                {
                    Id = existingIsRemote ? item.Id : existingId,
                    CreatedAtUnixMilliseconds = Math.Max(item.CreatedAtUnixMilliseconds, existingCreatedAt + 1)
                };
                existingReader.Close();

                using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                    UPDATE clipboard_items
                    SET id = $new_id,
                        origin_device_id = $origin_device_id,
                        text_content = $text_content,
                        created_at = $created_at,
                        synced = 0,
                        remote = 0
                    WHERE id = $id;
                    """;
                updateCommand.Parameters.AddWithValue("$new_id", updatedItem.Id);
                updateCommand.Parameters.AddWithValue("$id", existingId);
                updateCommand.Parameters.AddWithValue("$origin_device_id", updatedItem.OriginDeviceId);
                updateCommand.Parameters.AddWithValue("$text_content", updatedItem.TextContent!);
                updateCommand.Parameters.AddWithValue("$created_at", updatedItem.CreatedAtUnixMilliseconds);
                updateCommand.ExecuteNonQuery();
                transaction.Commit();

                return updatedItem;
            }
        }

        using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO clipboard_items
                    (id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote)
                VALUES
                    ($id, $origin_device_id, $type, $text_content, NULL, $content_hash, $created_at, 0, 0);
                """;
            insertCommand.Parameters.AddWithValue("$id", item.Id);
            insertCommand.Parameters.AddWithValue("$origin_device_id", item.OriginDeviceId);
            insertCommand.Parameters.AddWithValue("$type", TextTypeName);
            insertCommand.Parameters.AddWithValue("$text_content", item.TextContent!);
            insertCommand.Parameters.AddWithValue("$content_hash", item.ContentHash);
            insertCommand.Parameters.AddWithValue("$created_at", item.CreatedAtUnixMilliseconds);
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();

        return item;
    }

    public void UpsertRemoteText(ClipboardItem item, bool originatedLocally)
    {
        ArgumentNullException.ThrowIfNull(item);
        UpsertRemoteTextBatch([(item, originatedLocally)]);
    }

    public void UpsertRemoteTexts(IReadOnlyList<ClipboardItem> items, string localDeviceId)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDeviceId);
        if (items.Count == 0)
        {
            return;
        }

        var batch = new (ClipboardItem Item, bool OriginatedLocally)[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index] ?? throw new ArgumentException("A remote text item cannot be null.", nameof(items));
            batch[index] = (item, string.Equals(item.OriginDeviceId, localDeviceId, StringComparison.Ordinal));
        }

        UpsertRemoteTextBatch(batch);
    }

    private void UpsertRemoteTextBatch(IReadOnlyList<(ClipboardItem Item, bool OriginatedLocally)> items)
    {
        foreach (var (item, _) in items)
        {
            if (item.Type != ClipboardItemType.Text || string.IsNullOrWhiteSpace(item.TextContent))
            {
                throw new ArgumentException("A remote text history item must contain text.", nameof(items));
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var findCommand = connection.CreateCommand();
        findCommand.Transaction = transaction;
        findCommand.CommandText = """
            SELECT id, created_at, synced
            FROM clipboard_items
            WHERE type = $type AND content_hash = $content_hash
            LIMIT 1;
            """;
        findCommand.Parameters.AddWithValue("$type", TextTypeName);
        var findHashParameter = findCommand.Parameters.Add("$content_hash", SqliteType.Text);

        using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE clipboard_items
            SET origin_device_id = $origin_device_id,
                text_content = $text_content,
                created_at = $created_at,
                synced = 1,
                remote = $remote
            WHERE id = $id;
            """;
        var updateIdParameter = updateCommand.Parameters.Add("$id", SqliteType.Text);
        var updateOriginParameter = updateCommand.Parameters.Add("$origin_device_id", SqliteType.Text);
        var updateTextParameter = updateCommand.Parameters.Add("$text_content", SqliteType.Text);
        var updateCreatedParameter = updateCommand.Parameters.Add("$created_at", SqliteType.Integer);
        var updateRemoteParameter = updateCommand.Parameters.Add("$remote", SqliteType.Integer);

        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO clipboard_items
                (id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote)
            VALUES
                ($id, $origin_device_id, $type, $text_content, NULL, $content_hash, $created_at, 1, $remote);
            """;
        var insertIdParameter = insertCommand.Parameters.Add("$id", SqliteType.Text);
        var insertOriginParameter = insertCommand.Parameters.Add("$origin_device_id", SqliteType.Text);
        insertCommand.Parameters.AddWithValue("$type", TextTypeName);
        var insertTextParameter = insertCommand.Parameters.Add("$text_content", SqliteType.Text);
        var insertHashParameter = insertCommand.Parameters.Add("$content_hash", SqliteType.Text);
        var insertCreatedParameter = insertCommand.Parameters.Add("$created_at", SqliteType.Integer);
        var insertRemoteParameter = insertCommand.Parameters.Add("$remote", SqliteType.Integer);

        foreach (var (item, originatedLocally) in items)
        {
            findHashParameter.Value = item.ContentHash;
            string? existingId = null;
            long existingCreatedAt = 0;
            var existingIsSynced = false;
            using (var reader = findCommand.ExecuteReader())
            {
                if (reader.Read())
                {
                    existingId = reader.GetString(0);
                    existingCreatedAt = reader.GetInt64(1);
                    existingIsSynced = reader.GetInt32(2) != 0;
                }
            }

            if (existingId is not null)
            {
                if (existingIsSynced && item.CreatedAtUnixMilliseconds > existingCreatedAt)
                {
                    updateIdParameter.Value = existingId;
                    updateOriginParameter.Value = item.OriginDeviceId;
                    updateTextParameter.Value = item.TextContent!;
                    updateCreatedParameter.Value = item.CreatedAtUnixMilliseconds;
                    updateRemoteParameter.Value = originatedLocally ? 0 : 1;
                    updateCommand.ExecuteNonQuery();
                }

                continue;
            }

            insertIdParameter.Value = item.Id;
            insertOriginParameter.Value = item.OriginDeviceId;
            insertTextParameter.Value = item.TextContent!;
            insertHashParameter.Value = item.ContentHash;
            insertCreatedParameter.Value = item.CreatedAtUnixMilliseconds;
            insertRemoteParameter.Value = originatedLocally ? 0 : 1;
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public bool MarkSynced(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clipboard_items SET synced = 1 WHERE id = $id AND type = $type AND remote = 0;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", TextTypeName);
        return command.ExecuteNonQuery() == 1;
    }

    public ClipboardItem AddImage(ReadOnlyMemory<byte> pngBytes, string originDeviceId)
    {
        if (pngBytes.IsEmpty)
        {
            throw new ArgumentException("PNG image content cannot be empty.", nameof(pngBytes));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceId);

        var contentHash = ComputeContentHash(ImageTypeName, pngBytes.Span);
        var imagePath = Path.Combine(_objectsDirectory, $"{contentHash}.png");
        if (!File.Exists(imagePath))
        {
            Directory.CreateDirectory(_objectsDirectory);
            var temporaryPath = Path.Combine(_objectsDirectory, $".{contentHash}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, pngBytes.ToArray());
                try
                {
                    File.Move(temporaryPath, imagePath);
                }
                catch (IOException) when (File.Exists(imagePath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        var item = new ClipboardItem(
            Guid.NewGuid().ToString("N"),
            originDeviceId,
            ClipboardItemType.Image,
            null,
            imagePath,
            contentHash,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            false,
            false);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var findCommand = connection.CreateCommand())
        {
            findCommand.Transaction = transaction;
            findCommand.CommandText = """
                SELECT id, created_at
                FROM clipboard_items
                WHERE type = $type AND content_hash = $content_hash
                LIMIT 1;
                """;
            findCommand.Parameters.AddWithValue("$type", ImageTypeName);
            findCommand.Parameters.AddWithValue("$content_hash", contentHash);

            using var existingReader = findCommand.ExecuteReader();
            if (existingReader.Read())
            {
                var existingId = existingReader.GetString(0);
                var existingCreatedAt = existingReader.GetInt64(1);
                var updatedItem = item with
                {
                    Id = existingId,
                    CreatedAtUnixMilliseconds = Math.Max(item.CreatedAtUnixMilliseconds, existingCreatedAt + 1)
                };
                existingReader.Close();

                using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                    UPDATE clipboard_items
                    SET origin_device_id = $origin_device_id,
                        image_path = $image_path,
                        created_at = $created_at,
                        synced = 0,
                        remote = 0
                    WHERE id = $id;
                    """;
                updateCommand.Parameters.AddWithValue("$id", existingId);
                updateCommand.Parameters.AddWithValue("$origin_device_id", updatedItem.OriginDeviceId);
                updateCommand.Parameters.AddWithValue("$image_path", updatedItem.ImagePath!);
                updateCommand.Parameters.AddWithValue("$created_at", updatedItem.CreatedAtUnixMilliseconds);
                updateCommand.ExecuteNonQuery();
                transaction.Commit();

                return updatedItem;
            }
        }

        using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO clipboard_items
                    (id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote)
                VALUES
                    ($id, $origin_device_id, $type, NULL, $image_path, $content_hash, $created_at, 0, 0);
                """;
            insertCommand.Parameters.AddWithValue("$id", item.Id);
            insertCommand.Parameters.AddWithValue("$origin_device_id", item.OriginDeviceId);
            insertCommand.Parameters.AddWithValue("$type", ImageTypeName);
            insertCommand.Parameters.AddWithValue("$image_path", imagePath);
            insertCommand.Parameters.AddWithValue("$content_hash", item.ContentHash);
            insertCommand.Parameters.AddWithValue("$created_at", item.CreatedAtUnixMilliseconds);
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return item;
    }

    public ClipboardItem UpsertRemoteImage(string id, string originDeviceId, ReadOnlyMemory<byte> pngBytes, long createdAtUnixMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceId);
        if (pngBytes.IsEmpty)
        {
            throw new ArgumentException("PNG image content cannot be empty.", nameof(pngBytes));
        }

        var contentHash = ComputeContentHash(ImageTypeName, pngBytes.Span);
        var imagePath = Path.Combine(_objectsDirectory, $"{contentHash}.png");
        if (!File.Exists(imagePath))
        {
            Directory.CreateDirectory(_objectsDirectory);
            var temporaryPath = Path.Combine(_objectsDirectory, $".{contentHash}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, pngBytes.ToArray());
                try
                {
                    File.Move(temporaryPath, imagePath);
                }
                catch (IOException) when (File.Exists(imagePath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        var createdAt = createdAtUnixMilliseconds;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var findCommand = connection.CreateCommand())
        {
            findCommand.Transaction = transaction;
            findCommand.CommandText = "SELECT id, created_at FROM clipboard_items WHERE type = $type AND content_hash = $hash LIMIT 1;";
            findCommand.Parameters.AddWithValue("$type", ImageTypeName);
            findCommand.Parameters.AddWithValue("$hash", contentHash);
            using var reader = findCommand.ExecuteReader();
            if (reader.Read())
            {
                id = reader.GetString(0);
                createdAt = Math.Max(createdAt, reader.GetInt64(1));
                reader.Close();
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE clipboard_items SET origin_device_id=$origin, image_path=$path, created_at=$created, synced=1, remote=1 WHERE id=$id;";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$origin", originDeviceId);
                update.Parameters.AddWithValue("$path", imagePath);
                update.Parameters.AddWithValue("$created", createdAt);
                update.ExecuteNonQuery();
                transaction.Commit();
                return new ClipboardItem(id, originDeviceId, ClipboardItemType.Image, null, imagePath, contentHash, createdAt, true, true);
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO clipboard_items (id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote) VALUES ($id, $origin, $type, NULL, $path, $hash, $created, 1, 1);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$origin", originDeviceId);
            insert.Parameters.AddWithValue("$type", ImageTypeName);
            insert.Parameters.AddWithValue("$path", imagePath);
            insert.Parameters.AddWithValue("$hash", contentHash);
            insert.Parameters.AddWithValue("$created", createdAt);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return new ClipboardItem(id, originDeviceId, ClipboardItemType.Image, null, imagePath, contentHash, createdAt, true, true);
    }

    public bool MarkImageSynced(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clipboard_items SET synced = 1 WHERE id = $id AND type = $type AND remote = 0;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", ImageTypeName);
        return command.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<ClipboardItem> GetPendingImages(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote, last_used_at FROM clipboard_items WHERE type = $type AND synced = 0 AND remote = 0 ORDER BY created_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$type", ImageTypeName);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var items = new List<ClipboardItem>();
        while (reader.Read())
        {
            items.Add(ReadClipboardItem(reader));
        }

        return items;
    }

    public int GetTextCount()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clipboard_items WHERE type = $type;";
        command.Parameters.AddWithValue("$type", TextTypeName);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<ClipboardItem> GetRecentItems(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote, last_used_at
            FROM clipboard_items
            ORDER BY MAX(created_at, COALESCE(last_used_at, created_at)) DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var items = new List<ClipboardItem>();
        while (reader.Read())
        {
            items.Add(ReadClipboardItem(reader));
        }

        return items;
    }

    public IReadOnlyList<ClipboardItem> SearchItems(string query, int limit) =>
        SearchTextItems(query, limit);

    public IReadOnlyList<ClipboardItem> GetRecentTextItems(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote, last_used_at
            FROM clipboard_items
            WHERE type = $type
            ORDER BY MAX(created_at, COALESCE(last_used_at, created_at)) DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$type", TextTypeName);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var items = new List<ClipboardItem>();
        while (reader.Read())
        {
            var item = ReadClipboardItem(reader);
            if (!string.IsNullOrWhiteSpace(item.TextContent))
            {
                items.Add(item);
            }
        }

        return items;
    }

    public IReadOnlyList<ClipboardItem> SearchTextItems(string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (string.IsNullOrWhiteSpace(query))
        {
            return GetRecentTextItems(limit);
        }

        var escapedQuery = query
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote, last_used_at
            FROM clipboard_items
            WHERE type = $type AND text_content LIKE $query ESCAPE '\'
            ORDER BY MAX(created_at, COALESCE(last_used_at, created_at)) DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$type", TextTypeName);
        command.Parameters.AddWithValue("$query", $"%{escapedQuery}%");
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var items = new List<ClipboardItem>();
        while (reader.Read())
        {
            var item = ReadClipboardItem(reader);
            if (!string.IsNullOrWhiteSpace(item.TextContent))
            {
                items.Add(item);
            }
        }

        return items;
    }

    public ClipboardItem? FindById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote, last_used_at
            FROM clipboard_items
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadClipboardItem(reader);
    }

    public bool MarkUsed(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE clipboard_items
            SET last_used_at = MAX(
                $now,
                COALESCE((SELECT MAX(MAX(created_at, COALESCE(last_used_at, created_at))) + 1 FROM clipboard_items), $now),
                COALESCE((SELECT MAX(created_at, COALESCE(last_used_at, created_at)) + 1 FROM clipboard_items WHERE id = $id), $now))
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        var updated = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return updated;
    }

    public HistoryCleanupResult CleanupHistory(int retentionDays, long maximumContentBytes)
        => CleanupHistoryWithLimits(retentionDays, maximumItems: 0, maximumContentBytes: maximumContentBytes);

    public HistoryCleanupResult CleanupHistoryWithLimits(int retentionDays, int maximumItems, long maximumContentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionDays);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumItems);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumContentBytes);
        if (retentionDays == 0 && maximumItems == 0 && maximumContentBytes == 0)
        {
            return new HistoryCleanupResult(0, 0);
        }

        var cleanupTimer = Stopwatch.StartNew();
        var cutoff = retentionDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds()
            : (long?)null;
        var records = new List<CleanupRecord>();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, type, text_content, image_path, created_at, synced, remote
                FROM clipboard_items
                ORDER BY created_at DESC, id DESC;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new CleanupRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5) != 0 || reader.GetInt32(6) != 0));
            }
        }

        var deleteIds = new List<string>();
        var deletedImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retainedBytes = 0L;
        var retainedItems = 0;
        var retainedImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var contentBytes = GetContentBytes(record, retainedImagePaths);
            var isExpired = cutoff.HasValue && record.CreatedAt < cutoff.Value;
            var exceedsItemLimit = maximumItems > 0 && retainedItems >= maximumItems;
            var exceedsSizeLimit = maximumContentBytes > 0 && retainedBytes + contentBytes > maximumContentBytes;
            if (record.IsSynchronized && (isExpired || exceedsItemLimit || exceedsSizeLimit))
            {
                deleteIds.Add(record.Id);
                if (record.ImagePath is not null)
                {
                    deletedImagePaths.Add(record.ImagePath);
                }
                continue;
            }

            retainedBytes += contentBytes;
            retainedItems++;
            if (!string.Equals(record.Type, TextTypeName, StringComparison.OrdinalIgnoreCase)
                && record.ImagePath is not null)
            {
                retainedImagePaths.Add(record.ImagePath);
            }
        }

        if (deleteIds.Count == 0)
        {
            StartupPerformanceLog.Write("storage.history-cleanup", cleanupTimer.Elapsed, 0);
            return new HistoryCleanupResult(0, 0);
        }

        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM clipboard_items WHERE id = $id;";
            var idParameter = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var id in deleteIds)
            {
                idParameter.Value = id;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var reclaimedBytes = RemoveUnreferencedImageObjects(deletedImagePaths);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
        }
        catch (SqliteException exception)
        {
            Trace.TraceWarning("Could not compact clipboard history database after cleanup: {0}", exception.Message);
        }

        StartupPerformanceLog.Write("storage.history-cleanup", cleanupTimer.Elapsed, deleteIds.Count);
        return new HistoryCleanupResult(deleteIds.Count, reclaimedBytes);
    }

    private static long GetContentBytes(CleanupRecord record, HashSet<string> countedImagePaths)
    {
        if (string.Equals(record.Type, TextTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return record.TextContent is null ? 0 : Encoding.UTF8.GetByteCount(record.TextContent);
        }

        if (record.ImagePath is null || countedImagePaths.Contains(record.ImagePath))
        {
            return 0;
        }

        try
        {
            return new FileInfo(record.ImagePath).Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private long RemoveUnreferencedImageObjects(HashSet<string> candidatePaths)
    {
        if (!Directory.Exists(_objectsDirectory) || candidatePaths.Count == 0)
        {
            return 0;
        }

        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT image_path FROM clipboard_items WHERE type = $type AND image_path IS NOT NULL;";
            command.Parameters.AddWithValue("$type", ImageTypeName);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                referencedPaths.Add(Path.GetFullPath(reader.GetString(0)));
            }
        }

        long reclaimedBytes = 0;
        foreach (var candidatePath in candidatePaths)
        {
            var file = Path.GetFullPath(candidatePath);
            if (!string.Equals(Path.GetDirectoryName(file), Path.GetFullPath(_objectsDirectory), StringComparison.OrdinalIgnoreCase)
                || referencedPaths.Contains(file)
                || !File.Exists(file))
            {
                continue;
            }

            try
            {
                var length = new FileInfo(file).Length;
                File.Delete(file);
                reclaimedBytes += length;
            }
            catch (IOException exception)
            {
                Trace.TraceWarning("Could not remove unreferenced clipboard image {0}: {1}", file, exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                Trace.TraceWarning("Could not remove unreferenced clipboard image {0}: {1}", file, exception.Message);
            }
        }

        return reclaimedBytes;
    }

    private sealed record CleanupRecord(
        string Id,
        string Type,
        string? TextContent,
        string? ImagePath,
        long CreatedAt,
        bool IsSynchronized);

    private void InitializeDatabase()
    {
        var initializationTimer = Stopwatch.StartNew();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS clipboard_items (
                id TEXT PRIMARY KEY,
                origin_device_id TEXT NOT NULL,
                type TEXT NOT NULL,
                text_content TEXT,
                image_path TEXT,
                content_hash TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                synced INTEGER NOT NULL DEFAULT 0,
                remote INTEGER NOT NULL DEFAULT 0,
                last_used_at INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS ix_clipboard_items_created_at
                ON clipboard_items (created_at DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_clipboard_items_image_content_hash
                ON clipboard_items (content_hash)
                WHERE type = 'IMAGE';
            """;
        command.ExecuteNonQuery();
        EnsureLastUsedColumn(connection);
        EnsureTextUniquenessMigration(connection);
        StartupPerformanceLog.Write("storage.database-initialize", initializationTimer.Elapsed);
    }

    private static void EnsureLastUsedColumn(SqliteConnection connection)
    {
        using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(clipboard_items);";
        using var reader = columnsCommand.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "last_used_at", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE clipboard_items ADD COLUMN last_used_at INTEGER NULL;";
        alterCommand.ExecuteNonQuery();
    }

    private static void EnsureTextUniquenessMigration(SqliteConnection connection)
    {
        const int textUniquenessVersion = 1;
        var migrationTimer = Stopwatch.StartNew();
        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(versionCommand.ExecuteScalar()) >= textUniquenessVersion)
            {
                StartupPerformanceLog.Write("storage.text-cleanup", migrationTimer.Elapsed, 0);
                return;
            }
        }

        // Older releases created this index only after the cleanup had completed.
        // Use it as a completion marker so the first release with user_version support
        // does not repeat the full history scan for existing databases.
        using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'ux_clipboard_items_text_content_hash');";
            if (Convert.ToInt32(indexCommand.ExecuteScalar()) != 0)
            {
                using var markVersionCommand = connection.CreateCommand();
                markVersionCommand.CommandText = $"PRAGMA user_version = {textUniquenessVersion};";
                markVersionCommand.ExecuteNonQuery();
                StartupPerformanceLog.Write("storage.text-cleanup", migrationTimer.Elapsed, 0);
                return;
            }
        }

        var cleanupTimer = Stopwatch.StartNew();
        using var transaction = connection.BeginTransaction();
        var scannedRows = RemoveInvalidAndDuplicateTextItems(connection, transaction);
        using (var createIndexCommand = connection.CreateCommand())
        {
            createIndexCommand.Transaction = transaction;
            createIndexCommand.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_clipboard_items_text_content_hash
                    ON clipboard_items (content_hash)
                    WHERE type = 'TEXT';
                """;
            createIndexCommand.ExecuteNonQuery();
        }

        using (var markVersionCommand = connection.CreateCommand())
        {
            markVersionCommand.Transaction = transaction;
            markVersionCommand.CommandText = $"PRAGMA user_version = {textUniquenessVersion};";
            markVersionCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        StartupPerformanceLog.Write("storage.text-cleanup", cleanupTimer.Elapsed, scannedRows);
    }

    private static int RemoveInvalidAndDuplicateTextItems(SqliteConnection connection, SqliteTransaction transaction)
    {
        var invalidIds = new List<string>();
        var seenContentHashes = new HashSet<string>(StringComparer.Ordinal);
        var scannedRows = 0;
        using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = """
                SELECT id, text_content
                FROM clipboard_items
                WHERE type = $type
                ORDER BY created_at DESC, rowid DESC;
                """;
            readCommand.Parameters.AddWithValue("$type", TextTypeName);
            using var reader = readCommand.ExecuteReader();
            while (reader.Read())
            {
                scannedRows++;
                var id = reader.GetString(0);
                var text = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(text)
                    || !seenContentHashes.Add(ComputeContentHash(
                        TextTypeName,
                        Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC)))))
                {
                    invalidIds.Add(id);
                }
            }
        }

        using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM clipboard_items WHERE id = $id;";
        var idParameter = deleteCommand.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in invalidIds)
        {
            idParameter.Value = id;
            deleteCommand.ExecuteNonQuery();
        }

        return scannedRows;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static ClipboardItem ReadClipboardItem(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        ParseType(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.GetInt32(7) != 0,
        reader.GetInt32(8) != 0,
        reader.IsDBNull(9) ? null : reader.GetInt64(9));

    private static string ComputeContentHash(string type, ReadOnlySpan<byte> content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(type));
        hash.AppendData(content);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static ClipboardItemType ParseType(string type) => type switch
    {
        TextTypeName => ClipboardItemType.Text,
        ImageTypeName => ClipboardItemType.Image,
        _ => throw new InvalidDataException($"Unsupported clipboard item type '{type}'.")
    };
}
