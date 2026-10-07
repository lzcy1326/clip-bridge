using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipBridge.Windows;

public sealed class PasteManager
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const uint InputCount = 4;
    private const int PasteFocusAttempts = 12;
    private static readonly TimeSpan PasteFocusRetryDelay = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan PasteInputSettleDelay = TimeSpan.FromMilliseconds(20);

    private readonly Func<nint> _getForegroundWindow;
    private readonly Func<nint, bool> _isWindow;
    private readonly Func<nint, bool> _setForegroundWindow;
    private readonly Func<nint, nint> _getFocusedWindowHandle;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private nint _expectedFocusWindowHandle;

    public PasteManager()
        : this(GetForegroundWindow, IsWindow, SetForegroundWindow, Task.Delay, GetFocusedWindowHandleForWindow)
    {
    }

    internal PasteManager(
        Func<nint> getForegroundWindow,
        Func<nint, bool> isWindow,
        Func<nint, bool> setForegroundWindow,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<nint, nint>? getFocusedWindowHandle = null)
    {
        _getForegroundWindow = getForegroundWindow;
        _isWindow = isWindow;
        _setForegroundWindow = setForegroundWindow;
        _delay = delay;
        _getFocusedWindowHandle = getFocusedWindowHandle ?? (_ => nint.Zero);
    }

    public nint GetForegroundWindowHandle()
    {
        var foregroundWindowHandle = _getForegroundWindow();
        _expectedFocusWindowHandle = _getFocusedWindowHandle(foregroundWindowHandle);
        return foregroundWindowHandle;
    }

    public async Task<bool> RestoreForegroundWindowAsync(
        nint targetWindowHandle,
        CancellationToken cancellationToken = default)
    {
        if (targetWindowHandle == nint.Zero || !IsWindow(targetWindowHandle) || !SetForegroundWindow(targetWindowHandle))
        {
            return false;
        }

        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 80)
        {
            await Task.Delay(10, cancellationToken);
            if (GetForegroundWindow() == targetWindowHandle)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> PasteToAsync(nint targetWindowHandle, CancellationToken cancellationToken = default)
    {
        if (!await RestoreForegroundWindowForPasteAsync(targetWindowHandle, cancellationToken))
        {
            return false;
        }

        // Give the restored target a short settling interval before delivering Ctrl+V.
        await _delay(PasteInputSettleDelay, cancellationToken);

        var inputs = new[]
        {
            CreateKeyInput(VkControl, isKeyUp: false),
            CreateKeyInput(VkV, isKeyUp: false),
            CreateKeyInput(VkV, isKeyUp: true),
            CreateKeyInput(VkControl, isKeyUp: true)
        };

        if (SendInput(InputCount, inputs, Marshal.SizeOf<Input>()) == InputCount)
        {
            return true;
        }

        // If Windows accepted only part of the sequence, release both keys to avoid
        // leaving Ctrl or V logically held down.
        SendInput(2, [CreateKeyInput(VkV, isKeyUp: true), CreateKeyInput(VkControl, isKeyUp: true)], Marshal.SizeOf<Input>());
        return false;
    }

    internal async Task<bool> RestoreForegroundWindowForPasteAsync(
        nint targetWindowHandle,
        CancellationToken cancellationToken = default)
    {
        if (targetWindowHandle == nint.Zero || !_isWindow(targetWindowHandle))
        {
            return false;
        }

        for (var attempt = 0; attempt < PasteFocusAttempts; attempt++)
        {
            if (_getForegroundWindow() != targetWindowHandle)
            {
                _setForegroundWindow(targetWindowHandle);
            }

            if (_getForegroundWindow() == targetWindowHandle && IsExpectedInputFocusRestored(targetWindowHandle))
            {
                return true;
            }

            if (attempt + 1 < PasteFocusAttempts)
            {
                await _delay(PasteFocusRetryDelay, cancellationToken);
            }
        }

        return false;
    }

    private bool IsExpectedInputFocusRestored(nint targetWindowHandle)
    {
        if (_expectedFocusWindowHandle == nint.Zero || !_isWindow(_expectedFocusWindowHandle))
        {
            _expectedFocusWindowHandle = nint.Zero;
            return true;
        }

        return _getFocusedWindowHandle(targetWindowHandle) == _expectedFocusWindowHandle;
    }

    private static nint GetFocusedWindowHandleForWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return nint.Zero;
        }

        var threadId = GetWindowThreadProcessId(windowHandle, out _);
        if (threadId == 0)
        {
            return nint.Zero;
        }

        var threadInfo = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(threadId, ref threadInfo) ? threadInfo.FocusWindow : nint.Zero;
    }

    private static Input CreateKeyInput(ushort virtualKey, bool isKeyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = isKeyUp ? KeyEventKeyUp : 0
            }
        }
    };

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo threadInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwnerWindow;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public RectNative CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
