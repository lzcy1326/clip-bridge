using System.Windows;

namespace ClipBridge.Windows;

public sealed class ClipboardWriter
{
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            throw new ArgumentException("Clipboard text cannot be empty.", nameof(text));
        }

        Clipboard.SetText(text);
    }
}
