using System.IO;
using System.Text.Json;

namespace ClipBridge.App;

internal sealed record HistoryRetentionSettings(int RetentionDays, int MaximumItems, long MaximumContentBytes)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static HistoryRetentionSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<HistoryRetentionSettings>(json);
                if (loaded is not null && loaded.RetentionDays >= 0 && loaded.MaximumItems >= 0 && loaded.MaximumContentBytes >= 0)
                {
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.TryGetProperty(nameof(MaximumItems), out _)
                        ? loaded
                        : loaded with { MaximumItems = 300 };
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return new HistoryRetentionSettings(7, 300, 0);
    }

    public void Save(string path)
    {
        if (RetentionDays < 0 || MaximumItems < 0 || MaximumContentBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionDays), "清理限制不能小于 0。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
