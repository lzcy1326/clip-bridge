using System.Runtime.InteropServices;

namespace ClipBridge.Windows;

public sealed class PasteManager
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const uint InputCount = 4;

    public nint GetForegroundWindowHandle() => GetForegroundWindow();

    public async Task<bool> RestoreForegroundWindowAsync(
        nint targetWindowHandle,
        CancellationToken cancellationToken = default)
    {
        if (targetWindowHandle == nint.Zero || !IsWindow(targetWindowHandle) || !SetForegroundWindow(targetWindowHandle))
        {
            return false;
        }

        await Task.Delay(80, cancellationToken);
        return GetForegroundWindow() == targetWindowHandle;
    }

    public async Task<bool> PasteToAsync(nint targetWindowHandle, CancellationToken cancellationToken = default)
    {
        if (!await RestoreForegroundWindowAsync(targetWindowHandle, cancellationToken))
        {
            return false;
        }

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
}
