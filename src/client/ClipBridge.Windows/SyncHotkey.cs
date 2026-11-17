using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipBridge.Windows;

// A separate registration avoids intercepting the system's ordinary Ctrl+C.
public sealed class SyncHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int FirstId = 0xCB10;
    private const int SecondId = 0xCB11;
    private const uint NoRepeat = 0x4000;
    private readonly Window _window;
    private readonly Action _pressed;
    private HwndSource? _source;
    private nint _handle;
    private int _registeredId;
    private uint _modifiers;
    private uint _key;
    private bool _manual;

    public event Action<string>? RegistrationFailed;

    public SyncHotkey(Window window, Action pressed)
    {
        _window = window;
        _pressed = pressed;
        _window.SourceInitialized += OnSourceInitialized;
        if (new WindowInteropHelper(window).Handle != nint.Zero) OnSourceInitialized(window, EventArgs.Empty);
    }

    public bool Configure(bool manual, uint modifiers, uint key, out string? error)
    {
        error = null;
        if (!manual)
        {
            if (_registeredId != 0) UnregisterHotKey(_handle, _registeredId);
            _registeredId = 0;
            _manual = false;
            return true;
        }
        if ((modifiers & 2) == 0 || (modifiers & 13) == 0 || key == 0 || key == 0xC0)
        {
            error = "请选择 Ctrl 与 Alt、Shift 或 Win 组合的快捷键，且不要使用 Ctrl+`。";
            return false;
        }
        if (_manual && _registeredId != 0 && _modifiers == modifiers && _key == key) return true;
        if (_handle == nint.Zero)
        {
            _manual = true;
            _modifiers = modifiers;
            _key = key;
            return true;
        }
        var newId = _registeredId == FirstId ? SecondId : FirstId;
        if (!RegisterHotKey(_handle, newId, modifiers | NoRepeat, key))
        {
            error = "快捷键无法注册，可能已被其他程序占用：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        if (_registeredId != 0) UnregisterHotKey(_handle, _registeredId);
        _registeredId = newId;
        _manual = true;
        _modifiers = modifiers;
        _key = key;
        return true;
    }

    public void Suspend()
    {
        if (_registeredId != 0) UnregisterHotKey(_handle, _registeredId);
        _registeredId = 0;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProcedure);
        if (_manual && !Configure(true, _modifiers, _key, out var error))
            RegistrationFailed?.Invoke(error ?? "同步快捷键注册失败。");
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _registeredId != 0 && wParam.ToInt32() == _registeredId)
        {
            handled = true;
            _pressed();
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        _window.SourceInitialized -= OnSourceInitialized;
        if (_registeredId != 0) UnregisterHotKey(_handle, _registeredId);
        _registeredId = 0;
        _source?.RemoveHook(WindowProcedure);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
