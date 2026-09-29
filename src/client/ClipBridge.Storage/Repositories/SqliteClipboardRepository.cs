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

    public ClipboardItem AddText(string text, string originDeviceId)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(originDeviceId);

        var normalizedText = text.Normalize(NormalizationForm.FormC);
        var contentHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(TextTypeName + normalizedText)))
            .ToLowerInvariant();
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
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clipboard_items
                (id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote)
            VALUES
                ($id, $origin_device_id, $type, $text_content, NULL, $content_hash, $created_at, 0, 0);
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$origin_device_id", item.OriginDeviceId);
        command.Parameters.AddWithValue("$type", TextTypeName);
        command.Parameters.AddWithValue("$text_content", item.TextContent!);
        command.Parameters.AddWithValue("$content_hash", item.ContentHash);
        command.Parameters.AddWithValue("$created_at", item.CreatedAtUnixMilliseconds);
        command.ExecuteNonQuery();

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
            SELECT id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote
            FROM clipboard_items
            WHERE type = $type
            ORDER BY created_at DESC, rowid DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$type", TextTypeName);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var items = new List<ClipboardItem>();
        while (reader.Read())
        {
            items.Add(ReadClipboardItem(reader));
        }

        return items;
    }

    public ClipboardItem? FindById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, origin_device_id, type, text_content, image_path, content_hash, created_at, synced, remote
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
                remote INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_clipboard_items_created_at
                ON clipboard_items (created_at DESC);
            """;
        command.ExecuteNonQuery();
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
        reader.GetInt32(8) != 0);

    private static ClipboardItemType ParseType(string type) => type switch
    {
        TextTypeName => ClipboardItemType.Text,
        _ => throw new InvalidDataException($"Unsupported clipboard item type '{type}'.")
    };
}
