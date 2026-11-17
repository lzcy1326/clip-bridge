using System.Security.Cryptography;
using System.Text;
using ClipBridge.Core.Models;

namespace ClipBridge.Core.Services;

// Clipboard redirection can reflect a local copy back from another device.
// Prefer a recent local copy over a delayed reflection of the same content.
public sealed class ClipboardEchoGuard
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    private const int MaxEntries = 256;
    private readonly object _gate = new();
    private readonly Dictionary<(ClipboardItemType Type, string Hash), DateTimeOffset> _recent = [];
    private readonly TimeProvider _clock;

    public ClipboardEchoGuard(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public void RecordText(string text) => Record(ClipboardItemType.Text,
        Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC)));

    public void RecordImage(byte[] png) => Record(ClipboardItemType.Image, png);

    public bool IsRecentLocal(ClipboardItem item)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            return _recent.TryGetValue((item.Type, item.ContentHash), out var recorded)
                && now - recorded < Retention;
        }
    }

    private void Record(ClipboardItemType type, byte[] data)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.UTF8.GetBytes(type.ToString().ToUpperInvariant()));
        digest.AppendData(data);
        var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var key in _recent.Where(pair => now - pair.Value >= Retention).Select(pair => pair.Key).ToArray())
                _recent.Remove(key);
            if (_recent.Count >= MaxEntries && !_recent.ContainsKey((type, hash)))
                _recent.Remove(_recent.MinBy(pair => pair.Value).Key);
            _recent[(type, hash)] = now;
        }
    }
}
