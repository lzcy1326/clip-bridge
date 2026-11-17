using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ClipBridge.Core.Models;

namespace ClipBridge.App;

public sealed class HistoryListEntry : INotifyPropertyChanged
{
    public HistoryListEntry(ClipboardItem item, int sequenceNumber, byte[]? imageBytes = null)
    {
        Item = item;
        SequenceNumber = sequenceNumber;
        ImageBytes = imageBytes;
    }

    public ClipboardItem Item { get; }
    public int SequenceNumber { get; }
    public byte[]? ImageBytes { get; }
    private static readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> Previews = new();
    private static readonly SemaphoreSlim PreviewWorkers = new(2);
    private int _observing;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string? TextContent => Item.Type == ClipboardItemType.Files
        ? string.Join(Environment.NewLine, JsonSerializer.Deserialize<string[]>(Item.TextContent ?? "[]") ?? [])
        : Item.TextContent;
    public string? ImagePath => Item.ImagePath;
    public ClipboardItemType Type => Item.Type;
    public DateTimeOffset CreatedAt => Item.CreatedAt;
    public DateTimeOffset? LastUsedAt => Item.LastUsedAt;
    public string OriginDeviceId => Item.OriginDeviceId;

    public BitmapSource? Thumbnail
    {
        get
        {
            if (Type != ClipboardItemType.Image) return null;
            if (Previews.Count > 1000) Previews.Clear();
            var task = Previews.GetOrAdd(Item.ContentHash, _ => new(() => Task.Run(LoadPreviewAsync))).Value;
            if (task.IsCompletedSuccessfully) return task.Result;
            if (Interlocked.Exchange(ref _observing, 1) == 0) _ = ObservePreviewAsync(task);
            return null;
        }
    }

    private async Task<BitmapSource?> LoadPreviewAsync()
    {
        await PreviewWorkers.WaitAsync().ConfigureAwait(false);
        try
        {
            using var stream = ImageBytes is not null ? new MemoryStream(ImageBytes) : (Stream)new FileStream(
                ImagePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = 56;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
        finally { PreviewWorkers.Release(); }
    }

    private async Task ObservePreviewAsync(Task<BitmapSource?> task)
    {
        await task.ConfigureAwait(false);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            _ = dispatcher.BeginInvoke(new Action(() => PropertyChanged?.Invoke(this, new(nameof(Thumbnail)))));
    }
}
