using ClipBridge.Core.Models;

namespace ClipBridge.Core.Interfaces;

public interface IClipboardRepository
{
    ClipboardItem AddText(string text, string originDeviceId);

    int GetTextCount();

    ClipboardItem? FindById(string id);
}
