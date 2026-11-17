using System.Runtime.InteropServices;

namespace ClipBridge.Windows;

public readonly record struct CopyAttempt(uint PreviousSequence, uint? CopiedSequence);
public enum CopySnapshotResult { Ready, Changed, NoNewCopy, Files, Unsupported }

public sealed class CopySelectionCommand
{
    private readonly Func<int, bool> _keysHeld;
    private readonly Func<bool> _sendCopy;
    private readonly Func<uint> _sequence;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<nint> _foreground;

    public CopySelectionCommand() : this(AreShortcutKeysHeld, SendCopy, () => ClipboardWriter.CurrentSequenceNumber, Task.Delay, GetForegroundWindow) { }
    public static nint ForegroundWindow => GetForegroundWindow();

    internal CopySelectionCommand(Func<int, bool> keysHeld, Func<bool> sendCopy, Func<uint> sequence,
        Func<TimeSpan, CancellationToken, Task> delay, Func<nint>? foreground = null)
    {
        _keysHeld = keysHeld;
        _sendCopy = sendCopy;
        _sequence = sequence;
        _delay = delay;
        _foreground = foreground ?? (() => new nint(1));
    }

    // A null result means Ctrl+C did not change the clipboard.
    public async Task<CopyAttempt> CopyAsync(int shortcutKey, CancellationToken cancellationToken = default)
    {
        var target = _foreground();
        if (target == nint.Zero) throw new InvalidOperationException("未找到当前复制目标。");
        for (var i = 0; _keysHeld(shortcutKey); i++)
        {
            if (i >= 100) throw new InvalidOperationException("请松开同步快捷键后重试。");
            await _delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
        if (_foreground() != target) throw new InvalidOperationException("当前窗口已变化，请重新选择内容并使用同步快捷键。");
        var before = _sequence();
        if (!_sendCopy()) throw new InvalidOperationException("无法向当前程序发送复制命令。");
        for (var i = 0; i < 50; i++)
        {
            await _delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            var after = _sequence();
            if (after != before) return new(before, after);
        }
        return new(before, null);
    }

    public static CopySnapshotResult Evaluate(CopyAttempt copy, ClipboardSnapshot snapshot)
    {
        if (snapshot.Sequence != (copy.CopiedSequence ?? copy.PreviousSequence)) return CopySnapshotResult.Changed;
        if (copy.CopiedSequence is null) return CopySnapshotResult.NoNewCopy;
        if (snapshot.Kind == ClipboardSnapshotKind.Files) return CopySnapshotResult.Files;
        if (snapshot.Kind == ClipboardSnapshotKind.Unsupported
            || snapshot.Kind == ClipboardSnapshotKind.Text && string.IsNullOrWhiteSpace(snapshot.Text))
            return CopySnapshotResult.Unsupported;
        return CopySnapshotResult.Ready;
    }

    private static bool AreShortcutKeysHeld(int shortcutKey) =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, shortcutKey }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static bool SendCopy()
    {
        var inputs = new[] { Key(0x11, false), Key(0x43, false), Key(0x43, true), Key(0x11, true) };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length) return true;
        SendInput(2, [Key(0x43, true), Key(0x11, true)], Marshal.SizeOf<Input>());
        return false;
    }

    private static Input Key(ushort virtualKey, bool up) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = up ? 2u : 0u } }
    };

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public nuint ExtraInfo; }
}
