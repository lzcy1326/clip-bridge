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
    private string? _suppressedText;
    private DispatcherTimer? _clipboardRetryTimer;
    private bool _isRegistered;
    private bool _isDisposed;
    private bool _clipboardReadScheduled;
    private int _clipboardReadAttempts;
    private uint? _suppressedImageClipboardSequenceNumber;

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

    public event Action<byte[]>? ImageCaptured;

    public event Action? ClipboardReadFailed;

    public event Action<Exception>? ListenerFailed;

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
            if (TryReadClipboardImage(out var imageBytes))
            {
                ImageCaptured?.Invoke(imageBytes);
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

                TextCaptured?.Invoke(text);
                ResetClipboardReadRetry();
                return;
            }

            ResetClipboardReadRetry();
        }
        catch (ExternalException)
        {
            ScheduleClipboardReadRetry();
        }
    }

    private bool TryReadClipboardImage(out byte[] pngBytes)
    {
        pngBytes = [];
        var pngFormat = RegisterClipboardFormat("PNG");
        if (!OpenClipboard(_windowHandle))
        {
            throw new ExternalException("The clipboard is temporarily unavailable.", Marshal.GetLastWin32Error());
        }

        try
        {
            if (pngFormat != 0 && IsClipboardFormatAvailable(pngFormat)
                && TryCopyClipboardData(pngFormat, out var rawPng)
                && TryEncodeImage(rawPng, isDib: false, out pngBytes))
            {
                return true;
            }

            foreach (var dibFormat in new[] { CfDibV5, CfDib })
            {
                if (IsClipboardFormatAvailable(dibFormat)
                    && TryCopyClipboardData(dibFormat, out var dibBytes)
                    && TryEncodeImage(dibBytes, isDib: true, out pngBytes))
                {
                    return true;
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

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

    private void ScheduleClipboardReadRetry()
    {
        if (_isDisposed || _clipboardReadScheduled)
        {
            return;
        }

        _clipboardReadScheduled = true;
        var timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _clipboardRetryTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _clipboardRetryTimer = null;
            _clipboardReadScheduled = false;
            if (_isDisposed)
            {
                return;
            }

            if (++_clipboardReadAttempts > 5)
            {
                ResetClipboardReadRetry();
                ClipboardReadFailed?.Invoke();
                return;
            }

            ReadClipboardText();
        };
        timer.Start();
    }

    private void ResetClipboardReadRetry()
    {
        _clipboardRetryTimer?.Stop();
        _clipboardRetryTimer = null;
        _clipboardReadAttempts = 0;
        _clipboardReadScheduled = false;
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
