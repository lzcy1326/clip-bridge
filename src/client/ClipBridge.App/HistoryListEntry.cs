using ClipBridge.Core.Models;

namespace ClipBridge.App;

public sealed record HistoryListEntry(ClipboardItem Item, int SequenceNumber)
{
    public string? TextContent => Item.TextContent;

    public string? ImagePath => Item.ImagePath;

    public ClipboardItemType Type => Item.Type;

    public DateTimeOffset CreatedAt => Item.CreatedAt;

    public DateTimeOffset? LastUsedAt => Item.LastUsedAt;

    public string OriginDeviceId => Item.OriginDeviceId;
}
