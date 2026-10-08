using System.Globalization;
using System.Text;

namespace ClipBridge.Core.Diagnostics;

public static class StartupPerformanceLog
{
    private const long MaximumLogBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Configure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (Gate)
        {
            _path = Path.GetFullPath(path);
        }
    }

    public static void Write(string stage, TimeSpan elapsed, int? itemCount = null)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        var count = itemCount is null ? string.Empty : $"\titems={itemCount.Value.ToString(CultureInfo.InvariantCulture)}";
        Append(path, $"{DateTimeOffset.UtcNow:O}\t{stage}\t{elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms{count}{Environment.NewLine}");
    }

    public static void Write(string stage, TimeSpan elapsed, string detail)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        var safeDetail = detail.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTimeOffset.UtcNow:O}\t{stage}\t{elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms\t{safeDetail}{Environment.NewLine}";
        Append(path, line);
    }

    private static void Append(string path, string line)
    {
        lock (Gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                {
                    var previousPath = path + ".previous";
                    File.Move(path, previousPath, overwrite: true);
                }

                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Diagnostics must never prevent application startup or synchronization.
            }
            catch (Exception)
            {
                // Diagnostics must never prevent application startup or synchronization.
            }
        }
    }
}
