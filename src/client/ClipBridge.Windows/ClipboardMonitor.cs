using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipBridge.Windows;

public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;

    private readonly Window _window;
    private HwndSource? _source;
    private nint _windowHandle;
    private bool _isRegistered;
    private bool _isDisposed;

    public ClipboardMonitor(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closing += OnWindowClosing;

        if (new WindowInteropHelper(_window).Handle != nint.Zero)
        {
            RegisterListener();
        }
    }

    public event Action<string>? TextCaptured;

    public event Action? ClipboardReadFailed;

    public event Action<Exception>? ListenerFailed;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closing -= OnWindowClosing;

        if (_isRegistered)
        {
            RemoveClipboardFormatListener(_windowHandle);
            _isRegistered = false;
        }

        _source?.RemoveHook(WindowProcedure);
        _source = null;
        _windowHandle = nint.Zero;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => RegisterListener();

    private void RegisterListener()
    {
        _windowHandle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);

        if (_source is null)
        {
            ListenerFailed?.Invoke(new InvalidOperationException("The main window message source is unavailable."));
            return;
        }

        _source.AddHook(WindowProcedure);
        if (AddClipboardFormatListener(_windowHandle))
        {
            _isRegistered = true;
            return;
        }

        var error = new Win32Exception(Marshal.GetLastWin32Error());
        _source.RemoveHook(WindowProcedure);
        _source = null;
        _windowHandle = nint.Zero;
        ListenerFailed?.Invoke(error);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmClipboardUpdate)
        {
            ReadClipboardText();
        }

        return IntPtr.Zero;
    }

    private void ReadClipboardText()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                TextCaptured?.Invoke(Clipboard.GetText());
            }
        }
        catch (ExternalException)
        {
            ClipboardReadFailed?.Invoke();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e) => Dispose();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
