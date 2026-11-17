using System.IO;
using System.Text.Json;

namespace ClipBridge.App;

internal sealed record SyncShortcutSettings(bool Automatic = true, uint Modifiers = 6, uint VirtualKey = 0x43)
{
    public static SyncShortcutSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<SyncShortcutSettings>(File.ReadAllText(path)) ?? new(Automatic: false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged setting must not silently enable automatic uploads.
            return new(Automatic: false);
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, true);
    }
}
