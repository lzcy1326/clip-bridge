using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipBridge.Windows;

public sealed class ClipboardWriter : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);
    private const int MaxAttempts = 5;
    private readonly StaTaskRunner _worker = new("ClipBridge local clipboard writer");
    private readonly Action<string>? _setText;
    private readonly Action<BitmapSource>? _setImage;
    private HwndSource? _owner;

    public ClipboardWriter(Action<string>? setText = null, Action<BitmapSource>? setImage = null)
    {
        _setText = setText;
        _setImage = setImage;
    }

    // Raised before CloseClipboard, so the monitor can suppress our own notification.
    public event Action<uint>? Written;
    public static uint CurrentSequenceNumber => GetClipboardSequenceNumber();

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default, uint? expectedSequence = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return SetClipboardDataWithRetryAsync(() =>
        {
            CheckClipboardState(expectedSequence, cancellationToken);
            if (_setText is not null) _setText(text);
            else WriteNative([(13u, Encoding.Unicode.GetBytes(text + '\0'))], cancellationToken, expectedSequence);
        }, cancellationToken);
    }

    public async Task SetImageAsync(string imagePath, CancellationToken cancellationToken = default, uint? expectedSequence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var png = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
        await SetImageBytesAsync(png, cancellationToken, expectedSequence).ConfigureAwait(false);
    }

    public Task SetImageBytesAsync(byte[] png, CancellationToken cancellationToken = default, uint? expectedSequence = null) =>
        SetClipboardDataWithRetryAsync(() =>
        {
            using var stream = new MemoryStream(png);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bitmap = decoder.Frames[0];
            if (_setImage is not null)
            {
                CheckClipboardState(expectedSequence, cancellationToken);
                _setImage(bitmap);
            }
            else
            {
                var formats = new[] { (RegisterClipboardFormat("PNG"), png), (17u, CreateDib(bitmap)) };
                CheckClipboardState(expectedSequence, cancellationToken);
                WriteNative(formats, cancellationToken, expectedSequence);
            }
        }, cancellationToken);

    public Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0 || paths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("File paths cannot be empty.", nameof(paths));
        // CF_HDROP publishes names without opening or uploading the files.
        var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var data = new byte[20 + names.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data, 20);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1);
        names.CopyTo(data, 20);
        return SetClipboardDataWithRetryAsync(() => WriteNative([(15u, data)]), cancellationToken);
    }

    private async Task SetClipboardDataWithRetryAsync(Action write, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _worker.RunAsync(() => { write(); return true; }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ExternalException) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void CheckClipboardState(uint? expectedSequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedSequence is uint expected && GetClipboardSequenceNumber() != expected)
            throw new OperationCanceledException("The system clipboard changed before the synchronized write.");
    }

    private void WriteNative((uint Format, byte[] Data)[] formats, CancellationToken cancellationToken = default, uint? expectedSequence = null)
    {
        _owner ??= new HwndSource(new HwndSourceParameters("ClipBridge clipboard owner")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new nint(-3) // HWND_MESSAGE: never shows a desktop window.
        });
        var allocations = new nint[formats.Length];
        try
        {
            CheckClipboardState(expectedSequence, cancellationToken);
            // Allocate and fill global memory before opening the system clipboard.
            for (var index = 0; index < formats.Length; index++)
            {
                var (format, data) = formats[index];
                if (format == 0) throw new ExternalException("Cannot register clipboard format.");
                var memory = GlobalAlloc(0x0002, (nuint)data.Length);
                if (memory == nint.Zero) throw new OutOfMemoryException();
                allocations[index] = memory;
                var pointer = GlobalLock(memory);
                if (pointer == nint.Zero) throw new ExternalException("Cannot lock clipboard data.");
                try { Marshal.Copy(data, 0, pointer, data.Length); }
                finally { GlobalUnlock(memory); }
            }

            if (!OpenClipboard(_owner.Handle)) throw new ExternalException("Clipboard is busy.", Marshal.GetLastWin32Error());
            try
            {
                CheckClipboardState(expectedSequence, cancellationToken);
                if (!EmptyClipboard()) throw new ExternalException("Cannot empty clipboard.", Marshal.GetLastWin32Error());
                for (var index = 0; index < formats.Length; index++)
                {
                    if (SetClipboardData(formats[index].Format, allocations[index]) == nint.Zero)
                        throw new ExternalException("Cannot write clipboard data.", Marshal.GetLastWin32Error());
                    allocations[index] = nint.Zero; // Ownership transferred to Windows.
                }
                Written?.Invoke(GetClipboardSequenceNumber());
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            foreach (var memory in allocations)
                if (memory != nint.Zero) GlobalFree(memory);
        }
    }

    private static byte[] CreateDib(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var size = checked(stride * bitmap.PixelHeight);
        var dib = new byte[checked(124 + size)];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 124);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), bitmap.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), -bitmap.PixelHeight);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(16), 3);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), size);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(56), 0x73524742);
        var pixels = new byte[size];
        bitmap.CopyPixels(pixels, stride, 0);
        pixels.CopyTo(dib, 124);
        return dib;
    }

    public void Dispose() => _worker.Dispose();

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
