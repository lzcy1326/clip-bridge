using System.IO;
using Microsoft.Win32;

namespace ClipBridge.App;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipBridge";
    private const string BackgroundArgument = "--background";

    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var currentValue = runKey?.GetValue(ValueName) as string;
        return string.Equals(currentValue, GetCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的登录启动设置。");

        if (enabled)
        {
            runKey.SetValue(ValueName, GetCommand(), RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string GetCommand()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ClipBridge.exe");
        if (!File.Exists(appPath))
        {
            appPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定 ClipBridge 程序路径。");
        }

        return $"\"{appPath}\" {BackgroundArgument}";
    }
}
