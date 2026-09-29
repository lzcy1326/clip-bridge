using ClipBridge.Core.Models;

namespace ClipBridge.Core.Interfaces;

public interface IClipboardRepository
{
    ClipboardItem AddText(string text, string originDeviceId);

    int GetTextCount();

    IReadOnlyList<ClipboardItem> GetRecentTextItems(int limit);

    ClipboardItem? FindById(string id);
}
