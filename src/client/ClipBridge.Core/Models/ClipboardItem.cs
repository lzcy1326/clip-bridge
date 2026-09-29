namespace ClipBridge.Core.Models;

public enum ClipboardItemType
{
    Text
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
    bool Remote)
{
    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtUnixMilliseconds).ToLocalTime();
}
