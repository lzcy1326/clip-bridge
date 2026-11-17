using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ClipBridge.Core.Interfaces;
using ClipBridge.Core.Models;

namespace ClipBridge.Core.Services;

public sealed record LocalHistoryItem(ClipboardItem Item, byte[]? ImageBytes = null)
{
    public string? SearchText { get; } = GetSearchText(Item);
    private static string? GetSearchText(ClipboardItem item)
    {
        if (item.Type != ClipboardItemType.Files) return item.TextContent;
        try { return string.Join(Environment.NewLine, JsonSerializer.Deserialize<string[]>(item.TextContent ?? "[]") ?? []); }
        catch (JsonException) { return item.TextContent; }
    }
}

// The UI only reads this memory cache. Disk and database work have their own queue;
// no work item in this queue is allowed to wait for the synchronization server.
public sealed class LocalClipboardHistory : IAsyncDisposable
{
    private const int Limit = 1000;
    private readonly IClipboardRepository _repository;
    private readonly object _gate = new();
    private readonly List<LocalHistoryItem> _items = [];
    private readonly HashSet<string> _pending = [];
    private readonly Dictionary<string, string> _savedIds = [];
    private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private readonly Task _worker;
    private int _refreshQueued;
    public bool IsLoaded { get; private set; }

    public LocalClipboardHistory(IClipboardRepository repository)
    {
        _repository = repository;
        _worker = Task.Run(ProcessAsync);
        Refresh();
    }

    public event Action? Changed;
    public event Action<Exception>? Failed;

    public IReadOnlyList<LocalHistoryItem> Snapshot(string? query = null)
    {
        lock (_gate)
        {
            return _items.Where(entry => string.IsNullOrWhiteSpace(query)
                    || (entry.SearchText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(entry => Math.Max(entry.Item.CreatedAtUnixMilliseconds, entry.Item.LastUsedAtUnixMilliseconds ?? 0))
                .Take(Limit).ToArray();
        }
    }

    public void CaptureText(string text, string origin, bool allowAutomaticUpload = true)
    {
        if (!string.IsNullOrWhiteSpace(text))
            Capture(ClipboardItemType.Text, text, null, () => _repository.AddText(text, origin, allowAutomaticUpload)!, origin);
    }

    public void CaptureImage(byte[] png, string origin, bool allowAutomaticUpload = true) =>
        Capture(ClipboardItemType.Image, null, png, () => _repository.AddImage(png, origin, allowAutomaticUpload), origin);

    public void CaptureFiles(IReadOnlyList<string> paths, string origin)
    {
        var copy = paths.ToArray();
        if (copy.Length > 0)
            Capture(ClipboardItemType.Files, JsonSerializer.Serialize(copy), null, () => _repository.AddFiles(copy, origin), origin);
    }

    private void Capture(ClipboardItemType type, string? text, byte[]? png, Func<ClipboardItem> save, string origin)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.UTF8.GetBytes(type.ToString().ToUpperInvariant()));
        digest.AppendData(png ?? Encoding.UTF8.GetBytes(type == ClipboardItemType.Text ? text!.Normalize(NormalizationForm.FormC) : text!));
        var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        var item = new ClipboardItem(Guid.NewGuid().ToString("N"), origin, type, text, null, hash,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, false);
        lock (_gate)
        {
            _pending.Add(item.Id);
            _items.RemoveAll(entry => entry.Item.Type == type && entry.Item.ContentHash == hash);
            _items.Insert(0, new(item, png));
            if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
        }
        Changed?.Invoke();
        _work.Writer.TryWrite(() =>
        {
            var saved = save();
            lock (_gate)
            {
                _pending.Remove(item.Id);
                _savedIds[item.Id] = saved.Id;
                var index = _items.FindIndex(entry => entry.Item.Id == item.Id);
                if (index >= 0)
                {
                    // Keep the in-memory capture time/order and PNG until the cache is reloaded.
                    var replacement = new LocalHistoryItem(saved with
                    {
                        CreatedAtUnixMilliseconds = Math.Max(saved.CreatedAtUnixMilliseconds, item.CreatedAtUnixMilliseconds),
                        LastUsedAtUnixMilliseconds = _items[index].Item.LastUsedAtUnixMilliseconds ?? saved.LastUsedAtUnixMilliseconds
                    }, png);
                    _items[index] = replacement;
                    _items.RemoveAll(entry => entry.Item.Id == saved.Id && !ReferenceEquals(entry, replacement));
                }
                if (_savedIds.Count > Limit * 2) _savedIds.Clear();
                TrimImageCache();
            }
            Changed?.Invoke();
        });
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _work.Writer.TryWrite(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            var snapshot = _repository.GetRecentItems(Limit);
            lock (_gate)
            {
                var pending = _items.Where(entry => _pending.Contains(entry.Item.Id)).ToArray();
                var imageCache = _items.Where(entry => entry.ImageBytes is not null)
                    .ToDictionary(entry => entry.Item.Id, entry => entry.ImageBytes);
                _items.Clear();
                _items.AddRange(pending);
                foreach (var item in snapshot)
                {
                    if (pending.Any(entry => entry.Item.Type == item.Type && entry.Item.ContentHash == item.ContentHash)) continue;
                    _items.Add(new(item, imageCache.GetValueOrDefault(item.Id)));
                }
                IsLoaded = true;
                TrimImageCache();
            }
            Changed?.Invoke();
        });
    }

    public void MarkUsed(string id)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(entry => entry.Item.Id == id);
            if (index >= 0)
                _items[index] = _items[index] with { Item = _items[index].Item with { LastUsedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } };
        }
        _work.Writer.TryWrite(() => _repository.MarkUsed(_savedIds.GetValueOrDefault(id, id)));
    }

    private void TrimImageCache()
    {
        long bytes = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].ImageBytes is not { } png) continue;
            bytes += png.Length;
            if (bytes > 32 * 1024 * 1024 && !_pending.Contains(_items[i].Item.Id))
                _items[i] = _items[i] with { ImageBytes = null };
        }
    }

    private async Task ProcessAsync()
    {
        await foreach (var action in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { action(); }
            catch (Exception exception) { Failed?.Invoke(exception); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _work.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }
}
