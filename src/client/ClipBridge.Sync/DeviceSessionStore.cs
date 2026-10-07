using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipBridge.Sync;

internal sealed class DeviceSessionStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public DeviceSessionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<DeviceSession?> LoadAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            try
            {
                await using var stream = File.OpenRead(_path);
                var persisted = await JsonSerializer.DeserializeAsync<PersistedDeviceSession>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (persisted is null || string.IsNullOrWhiteSpace(persisted.ServerAddress)
                    || !DeviceCodeStore.IsValid(persisted.DeviceCode) || persisted.DeviceId <= 0
                    || string.IsNullOrWhiteSpace(persisted.ProtectedCredential))
                {
                    return null;
                }

                return new DeviceSession(
                    persisted.ServerAddress,
                    persisted.DeviceCode,
                    persisted.DeviceId,
                    WindowsCredentialProtection.Unprotect(persisted.ProtectedCredential),
                    persisted.Cursor,
                    DeviceCodeStore.IsValid(persisted.PeerDeviceCode) ? persisted.PeerDeviceCode : null);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!DeviceCodeStore.IsValid(session.DeviceCode) || session.DeviceId <= 0 || string.IsNullOrWhiteSpace(session.Credential))
        {
            throw new ArgumentException("设备同步凭据无效。", nameof(session));
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            var persisted = new PersistedDeviceSession(
                session.ServerAddress,
                session.DeviceCode,
                session.DeviceId,
                WindowsCredentialProtection.Protect(session.Credential),
                session.Cursor,
                session.PeerDeviceCode);
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(persisted), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private sealed record PersistedDeviceSession(
        string ServerAddress,
        string DeviceCode,
        long DeviceId,
        string ProtectedCredential,
        string? Cursor,
        string? PeerDeviceCode = null);
}

internal sealed record DeviceSession(
    string ServerAddress,
    string DeviceCode,
    long DeviceId,
    string Credential,
    string? Cursor,
    string? PeerDeviceCode = null);

internal static class WindowsCredentialProtection
{
    public static string Protect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("设备凭证仅支持在 Windows 上加密。");
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        var input = CreateBlob(bytes);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var protectedBytes = new byte[output.Size];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Unprotect(string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("设备凭证仅支持在 Windows 上解密。");
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("设备凭证存储格式无效。", exception);
        }

        var input = CreateBlob(encrypted);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, output.Size);
                try
                {
                    return new UTF8Encoding(false, true).GetString(bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = data };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
