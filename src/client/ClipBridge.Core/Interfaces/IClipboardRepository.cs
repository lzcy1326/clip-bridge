using ClipBridge.Core.Models;

namespace ClipBridge.Core.Interfaces;

public interface IClipboardRepository
{
    ClipboardItem? AddText(string text, string originDeviceId, bool allowAutomaticUpload = true);

    void UpsertRemoteText(ClipboardItem item, bool originatedLocally);

    void UpsertRemoteTexts(IReadOnlyList<ClipboardItem> items, string localDeviceId);

    bool MarkSynced(string id);

    ClipboardItem AddImage(ReadOnlyMemory<byte> pngBytes, string originDeviceId, bool allowAutomaticUpload = true);

    ClipboardItem AddFiles(IReadOnlyList<string> paths, string originDeviceId);

    IReadOnlyList<ClipboardItem> GetPendingTexts(int limit, long sinceUnixMilliseconds = 0);

    ClipboardItem UpsertRemoteImage(string id, string originDeviceId, ReadOnlyMemory<byte> pngBytes, long createdAtUnixMilliseconds);

    bool MarkImageSynced(string id);

    IReadOnlyList<ClipboardItem> GetPendingImages(int limit, long sinceUnixMilliseconds = 0);

    int GetTextCount();

    IReadOnlyList<ClipboardItem> GetRecentItems(int limit);

    IReadOnlyList<ClipboardItem> SearchItems(string query, int limit);

    IReadOnlyList<ClipboardItem> GetRecentTextItems(int limit);

    IReadOnlyList<ClipboardItem> SearchTextItems(string query, int limit);

    ClipboardItem? FindById(string id);

    bool MarkUsed(string id);
}
