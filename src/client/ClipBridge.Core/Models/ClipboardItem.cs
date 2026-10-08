namespace ClipBridge.Core.Models;

public enum ClipboardItemType
{
    Text,
    Image
}

public sealed record ClipboardItem(
    string Id,
    string OriginDeviceId,
    ClipboardItemType Type,
    string? TextContent,
    string? ImagePath,
    string ContentHash,
    long CreatedAtUnixMilliseconds,
    bool Synced,
    bool Remote,
    long? LastUsedAtUnixMilliseconds = null)
{
    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUnixMilliseconds).ToLocalTime();

    public DateTimeOffset? LastUsedAt => LastUsedAtUnixMilliseconds is long timestamp
        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime()
        : null;
}
