using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipBridge.Windows;

public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xCB01;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkOem3 = 0xC0;

    private readonly Window _window;
    private readonly Action _onPressed;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private bool _isRegistered;
    private bool _isDisposed;

    public GlobalHotkey(Window window, Action onPressed)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _onPressed = onPressed ?? throw new ArgumentNullException(nameof(onPressed));
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnWindowClosed;

        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            Register();
        }
    }

    public event Action<Exception>? RegistrationFailed;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnWindowClosed;

        if (_isRegistered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
            _isRegistered = false;
        }

        _source?.RemoveHook(WindowProcedure);
        _source = null;
        _windowHandle = IntPtr.Zero;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Register();

    private void Register()
    {
        _windowHandle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        if (_source is null)
        {
            RegistrationFailed?.Invoke(new InvalidOperationException("The main window message source is unavailable."));
            return;
        }

        _source.AddHook(WindowProcedure);
        if (RegisterHotKey(_windowHandle, HotkeyId, ModControl | ModNoRepeat, VkOem3))
        {
            _isRegistered = true;
            return;
        }

        var error = new Win32Exception(Marshal.GetLastWin32Error());
        _source.RemoveHook(WindowProcedure);
        _source = null;
        _windowHandle = IntPtr.Zero;
        RegistrationFailed?.Invoke(error);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _onPressed();
        }

        return IntPtr.Zero;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
