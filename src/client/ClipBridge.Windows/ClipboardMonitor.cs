using System.ComponentModel;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipBridge.Windows;

public enum ClipboardSnapshotKind { Text, Image, Files, Unsupported }

public sealed record ClipboardSnapshot(ClipboardSnapshotKind Kind, uint Sequence, string? Text = null, byte[]? Image = null);

public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const uint BiBitfields = 3;
    private const uint BiAlphaBitfields = 6;

    private readonly Window _window;
    private HwndSource? _source;
    private nint _windowHandle;
    private volatile string? _suppressedText;
    private readonly StaTaskRunner _reader = new("ClipBridge local clipboard reader");
    private int _readRequested;
    private int _readerRunning;
    private int _copyReadGeneration;
    private uint _suppressedSequence;
    private uint _observedSequence;
    private uint _captureSequence;
    private bool _isRegistered;
    private volatile bool _isDisposed;
    private int _clipboardReadAttempts;
    private uint? _suppressedImageClipboardSequenceNumber;

    public ClipboardMonitor(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _observedSequence = GetClipboardSequenceNumber();
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closing += OnWindowClosing;

        if (new WindowInteropHelper(_window).Handle != nint.Zero)
        {
            RegisterListener();
        }
    }

    public event Action<string>? TextCaptured;

    public event Action<byte[]>? ImageCaptured;

    public event Action<string[]>? FilesCaptured;

    public event Action? ClipboardChanged;

    public uint ObservedSequence => Volatile.Read(ref _observedSequence);
    public uint CaptureSequence => Volatile.Read(ref _captureSequence);

    public static bool HasImageFormat()
    {
        const uint cfFileDrop = 15;
        const uint cfBitmap = 2;
        var png = RegisterClipboardFormat("PNG");
        return !IsClipboardFormatAvailable(cfFileDrop)
            && (png != 0 && IsClipboardFormatAvailable(png)
                || IsClipboardFormatAvailable(CfDibV5)
                || IsClipboardFormatAvailable(CfDib)
                || IsClipboardFormatAvailable(cfBitmap));
    }

    public void SuppressSequence(uint sequence)
    {
        Volatile.Write(ref _suppressedSequence, sequence);
        Volatile.Write(ref _observedSequence, sequence);
    }

    public event Action? ClipboardReadFailed;

    public event Action<Exception>? ListenerFailed;

    public Task<ClipboardSnapshot> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
        _reader.RunAsync(() =>
        {
            var sequence = GetClipboardSequenceNumber();
            ClipboardSnapshot snapshot;
            if (Clipboard.ContainsFileDropList())
                snapshot = new(ClipboardSnapshotKind.Files, sequence);
            else if (TryReadClipboardImage(out var png))
                snapshot = new(ClipboardSnapshotKind.Image, sequence, Image: png);
            else if (Clipboard.ContainsText())
                snapshot = new(ClipboardSnapshotKind.Text, sequence, Text: Clipboard.GetText());
            else
                snapshot = new(ClipboardSnapshotKind.Unsupported, sequence);

            if (sequence != GetClipboardSequenceNumber())
                throw new InvalidOperationException("读取时剪贴板已变化，请重新按同步快捷键。");
            return snapshot;
        }, cancellationToken);

    public void SuppressTextCapture(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _suppressedText = text;
    }

    public void CancelTextCaptureSuppression(string text)
    {
        if (string.Equals(_suppressedText, text, StringComparison.Ordinal))
        {
            _suppressedText = null;
        }
    }

    public void SuppressCurrentImageClipboardUpdate()
    {
        var sequenceNumber = GetClipboardSequenceNumber();
        _suppressedImageClipboardSequenceNumber = sequenceNumber == 0 ? null : sequenceNumber;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _reader.Dispose();
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
            var sequence = GetClipboardSequenceNumber();
            Volatile.Write(ref _observedSequence, sequence);
            if (sequence != Volatile.Read(ref _suppressedSequence))
            {
                ClipboardChanged?.Invoke();
                ScheduleCaptureAfterCopy();
            }
        }

        return IntPtr.Zero;
    }

    private async void ScheduleCaptureAfterCopy()
    {
        var generation = Interlocked.Increment(ref _copyReadGeneration);
        // Give the foreground application the first opportunity to copy or paste.
        await Task.Delay(100).ConfigureAwait(false);
        if (!_isDisposed && generation == Volatile.Read(ref _copyReadGeneration)) QueueClipboardRead();
    }

    private void ReadClipboardText()
    {
        var sequence = GetClipboardSequenceNumber();
        if (_isDisposed || sequence == Volatile.Read(ref _suppressedSequence)) return;
        if (_suppressedImageClipboardSequenceNumber is uint suppressedSequenceNumber)
        {
            if (GetClipboardSequenceNumber() == suppressedSequenceNumber)
            {
                ResetClipboardReadRetry();
                return;
            }

            _suppressedImageClipboardSequenceNumber = null;
        }

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (sequence == GetClipboardSequenceNumber()) { Volatile.Write(ref _captureSequence, sequence); FilesCaptured?.Invoke(files); }
                else QueueClipboardRead();
                ResetClipboardReadRetry();
                return;
            }
            if (TryReadClipboardImage(out var imageBytes))
            {
                if (sequence == GetClipboardSequenceNumber()) { Volatile.Write(ref _captureSequence, sequence); ImageCaptured?.Invoke(imageBytes); }
                else QueueClipboardRead();
                ResetClipboardReadRetry();
                return;
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (_suppressedText is not null)
                {
                    var shouldSuppress = string.Equals(_suppressedText, text, StringComparison.Ordinal);
                    _suppressedText = null;
                    if (shouldSuppress)
                    {
                        return;
                    }
                }

                if (sequence == GetClipboardSequenceNumber()) { Volatile.Write(ref _captureSequence, sequence); TextCaptured?.Invoke(text); }
                else QueueClipboardRead();
                ResetClipboardReadRetry();
                return;
            }

            ResetClipboardReadRetry();
        }
        catch (ExternalException)
        {
            ScheduleClipboardReadRetry();
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning("Clipboard capture failed: {0}", exception.Message);
            ClipboardReadFailed?.Invoke();
        }
    }

    private async void QueueClipboardRead()
    {
        if (_isDisposed) return;
        Interlocked.Exchange(ref _readRequested, 1);
        if (Interlocked.CompareExchange(ref _readerRunning, 1, 0) != 0) return;
        try
        {
            do
            {
                Interlocked.Exchange(ref _readRequested, 0);
                await _reader.RunAsync(() => { ReadClipboardText(); return true; }).ConfigureAwait(false);
            } while (!_isDisposed && Volatile.Read(ref _readRequested) != 0);
        }
        catch (Exception exception)
        {
            if (!_isDisposed) System.Diagnostics.Trace.TraceWarning("Clipboard reader failed: {0}", exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _readerRunning, 0);
            if (!_isDisposed && Volatile.Read(ref _readRequested) != 0) QueueClipboardRead();
        }
    }

    private bool TryReadClipboardImage(out byte[] pngBytes)
    {
        pngBytes = [];
        byte[]? rawImage = null;
        var isDib = false;
        var pngFormat = RegisterClipboardFormat("PNG");
        // Most copies are text. Avoid opening and holding the global clipboard
        // for image inspection when no image format is advertised.
        if ((pngFormat == 0 || !IsClipboardFormatAvailable(pngFormat))
            && !IsClipboardFormatAvailable(CfDibV5)
            && !IsClipboardFormatAvailable(CfDib)
            && !IsClipboardFormatAvailable(2)) // CF_BITMAP
            return false;
        if (!OpenClipboard(_windowHandle))
        {
            throw new ExternalException("The clipboard is temporarily unavailable.", Marshal.GetLastWin32Error());
        }

        try
        {
            if (pngFormat != 0 && IsClipboardFormatAvailable(pngFormat)
                && TryCopyClipboardData(pngFormat, out var rawPng))
            {
                rawImage = rawPng;
            }

            foreach (var dibFormat in new[] { CfDibV5, CfDib })
            {
                if (rawImage is null && IsClipboardFormatAvailable(dibFormat)
                    && TryCopyClipboardData(dibFormat, out var dibBytes))
                {
                    rawImage = dibBytes;
                    isDib = true;
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

        // Encoding must not hold the system clipboard open or block local writes.
        if (rawImage is not null && TryEncodeImage(rawImage, isDib, out pngBytes)) return true;

        // Some applications publish only CF_BITMAP. WPF can still decode that fallback.
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is not null)
            {
                pngBytes = EncodePng(MakeVisibleDibImage(image));
                return true;
            }
        }

        return false;
    }

    private static bool TryCopyClipboardData(uint format, out byte[] bytes)
    {
        bytes = [];
        var handle = GetClipboardData(format);
        if (handle == nint.Zero)
        {
            return false;
        }

        var size = GlobalSize(handle).ToUInt64();
        if (size == 0 || size > 100 * 1024 * 1024)
        {
            return false;
        }

        var pointer = GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            return false;
        }

        try
        {
            bytes = new byte[checked((int)size)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool TryEncodeImage(byte[] imageBytes, bool isDib, out byte[] pngBytes)
    {
        pngBytes = [];
        try
        {
            using var stream = new MemoryStream(isDib ? WrapDibInBitmapFile(imageBytes) : imageBytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            var frame = decoder.Frames[0];
            pngBytes = EncodePng(isDib ? MakeVisibleDibImage(frame) : frame);
            return pngBytes.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static byte[] WrapDibInBitmapFile(byte[] dibBytes)
    {
        if (dibBytes.Length < 40)
        {
            throw new InvalidDataException("The clipboard DIB header is incomplete.");
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(0, 4));
        if (headerSize < 40 || headerSize > dibBytes.Length)
        {
            throw new InvalidDataException("The clipboard DIB header size is invalid.");
        }

        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dibBytes.AsSpan(14, 2));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(16, 4));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.AsSpan(32, 4));
        var paletteEntries = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0;
        var externalMasks = headerSize == 40
            ? compression == BiAlphaBitfields ? 16u : compression == BiBitfields ? 12u : 0u
            : 0u;
        var pixelOffset = checked(14u + headerSize + externalMasks + (paletteEntries * 4));
        if (pixelOffset > 14u + dibBytes.Length)
        {
            throw new InvalidDataException("The clipboard DIB pixel data offset is invalid.");
        }

        var bitmapBytes = new byte[checked(14 + dibBytes.Length)];
        bitmapBytes[0] = (byte)'B';
        bitmapBytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bitmapBytes.AsSpan(2, 4), checked((uint)bitmapBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bitmapBytes.AsSpan(10, 4), pixelOffset);
        dibBytes.CopyTo(bitmapBytes, 14);
        return bitmapBytes;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private async void ScheduleClipboardReadRetry()
    {
        if (_isDisposed) return;
        if (++_clipboardReadAttempts > 5)
        {
            ResetClipboardReadRetry();
            ClipboardReadFailed?.Invoke();
            return;
        }
        await Task.Delay(40).ConfigureAwait(false);
        QueueClipboardRead();
    }

    private void ResetClipboardReadRetry()
    {
        _clipboardReadAttempts = 0;
    }

    private static BitmapSource MakeVisibleDibImage(BitmapSource image)
    {
        var bgraImage = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = checked(bgraImage.PixelWidth * 4);
        var pixels = new byte[checked(stride * bgraImage.PixelHeight)];
        bgraImage.CopyPixels(pixels, stride, 0);

        var hasNonzeroAlpha = false;
        for (var alphaOffset = 3; alphaOffset < pixels.Length; alphaOffset += 4)
        {
            if (pixels[alphaOffset] != 0)
            {
                hasNonzeroAlpha = true;
                break;
            }
        }

        if (hasNonzeroAlpha)
        {
            return image;
        }

        for (var alphaOffset = 3; alphaOffset < pixels.Length; alphaOffset += 4)
        {
            pixels[alphaOffset] = byte.MaxValue;
        }

        var opaqueImage = BitmapSource.Create(
            bgraImage.PixelWidth,
            bgraImage.PixelHeight,
            bgraImage.DpiX,
            bgraImage.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        opaqueImage.Freeze();
        return opaqueImage;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!e.Cancel)
        {
            Dispose();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
