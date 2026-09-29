using System.Windows;
using System.IO;
using System.Windows.Media.Imaging;

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

    public void SetImage(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("The clipboard history image file was not found.", imagePath);
        }

        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        Clipboard.SetImage(bitmap);
    }
}
