using System.Windows;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ClipBridge.Windows;

public sealed class ClipboardWriter
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);
    private const int MaxAttempts = 5;
    private readonly Action<string> _setText;
    private readonly Action<BitmapSource> _setImage;

    public ClipboardWriter(Action<string>? setText = null, Action<BitmapSource>? setImage = null)
    {
        _setText = setText ?? Clipboard.SetText;
        _setImage = setImage ?? Clipboard.SetImage;
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            throw new ArgumentException("Clipboard text cannot be empty.", nameof(text));
        }

        return SetClipboardDataWithRetryAsync(() => _setText(text), cancellationToken);
    }

    public async Task SetImageAsync(string imagePath, CancellationToken cancellationToken = default)
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
        await SetClipboardDataWithRetryAsync(() => _setImage(bitmap), cancellationToken);
    }

    private static async Task SetClipboardDataWithRetryAsync(Action write, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                write();
                return;
            }
            catch (ExternalException) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }
}
