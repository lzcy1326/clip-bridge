using System.Security.Cryptography;
using System.Text;
using ClipBridge.Core.Interfaces;
using ClipBridge.Core.Models;
using Microsoft.Data.Sqlite;

namespace ClipBridge.Storage.Repositories;

public sealed class SqliteClipboardRepository : IClipboardRepository
{
    private const string TextTypeName = "TEXT";
    private readonly string _connectionString;

    public SqliteClipboardRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
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
        var contentHash = ComputeContentHash(normalizedText);
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
                SELECT id, created_at
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
                        text_content = $text_content,
                        created_at = $created_at,
                        synced = 0,
                        remote = 0
                    WHERE id = $id;
                    """;
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

    public int GetTextCount()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clipboard_items WHERE type = $type;";
        command.Parameters.AddWithValue("$type", TextTypeName);
        return Convert.ToInt32(command.ExecuteScalar());
    }

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
            WHERE id = $id AND type = $type;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", TextTypeName);
        var updated = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return updated;
    }

    private void InitializeDatabase()
    {
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
            """;
        command.ExecuteNonQuery();
        EnsureLastUsedColumn(connection);
        RemoveInvalidAndDuplicateTextItems(connection);
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

    private static void RemoveInvalidAndDuplicateTextItems(SqliteConnection connection)
    {
        var invalidIds = new List<string>();
        var seenContentHashes = new HashSet<string>(StringComparer.Ordinal);
        using (var readCommand = connection.CreateCommand())
        {
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
                var id = reader.GetString(0);
                var text = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(text) || !seenContentHashes.Add(ComputeContentHash(text.Normalize(NormalizationForm.FormC))))
                {
                    invalidIds.Add(id);
                }
            }
        }

        using var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText = "DELETE FROM clipboard_items WHERE id = $id;";
        var idParameter = deleteCommand.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in invalidIds)
        {
            idParameter.Value = id;
            deleteCommand.ExecuteNonQuery();
        }

        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_clipboard_items_text_content_hash
                ON clipboard_items (content_hash)
                WHERE type = 'TEXT';
            """;
        indexCommand.ExecuteNonQuery();
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

    private static string ComputeContentHash(string normalizedText) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(TextTypeName + normalizedText)))
        .ToLowerInvariant();

    private static ClipboardItemType ParseType(string type) => type switch
    {
        TextTypeName => ClipboardItemType.Text,
        _ => throw new InvalidDataException($"Unsupported clipboard item type '{type}'.")
    };
}
