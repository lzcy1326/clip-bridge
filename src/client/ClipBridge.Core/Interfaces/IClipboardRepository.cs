using ClipBridge.Core.Models;

namespace ClipBridge.Core.Interfaces;

public interface IClipboardRepository
{
    ClipboardItem? AddText(string text, string originDeviceId);

    ClipboardItem AddImage(ReadOnlyMemory<byte> pngBytes, string originDeviceId);

    int GetTextCount();

    IReadOnlyList<ClipboardItem> GetRecentItems(int limit);

    IReadOnlyList<ClipboardItem> SearchItems(string query, int limit);

    IReadOnlyList<ClipboardItem> GetRecentTextItems(int limit);

    IReadOnlyList<ClipboardItem> SearchTextItems(string query, int limit);

    ClipboardItem? FindById(string id);

    bool MarkUsed(string id);
}
