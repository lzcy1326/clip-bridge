using System.Buffers.Binary;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipBridge.Sync;

/// <summary>
/// Stores the public, six-character device discovery code. This code is not an
/// authentication credential; authorization is handled by the pairing flow.
/// </summary>
public sealed class DeviceCodeStore
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static readonly Regex ValidCode = new("^[A-Z0-9]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _path;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public DeviceCodeStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path))
            {
                var saved = (await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false)).Trim();
                if (IsValid(saved))
                {
                    return saved;
                }
            }

            var macAddress = GetPrimaryMacAddress();
            var code = CreateCodeFromMacAddress(macAddress);
            await PersistAsync(code, cancellationToken).ConfigureAwait(false);
            return code;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SetAsync(string code, CancellationToken cancellationToken = default)
    {
        if (!IsValid(code))
        {
            throw new ArgumentException("设备识别码必须是 6 位大写字母或数字。", nameof(code));
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistAsync(code, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public static bool IsValid(string? code) => code is not null && ValidCode.IsMatch(code);

    public static string CreateCodeFromMacAddress(string macAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);
        var normalized = new string(macAddress.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalized.Length < 12)
        {
            throw new ArgumentException("MAC 地址格式无效。", nameof(macAddress));
        }

        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(normalized));
        var value = BinaryPrimitives.ReadUInt64BigEndian(digest);
        Span<char> code = stackalloc char[6];
        for (var index = code.Length - 1; index >= 0; index--)
        {
            code[index] = Alphabet[(int)(value % (ulong)Alphabet.Length)];
            value /= (ulong)Alphabet.Length;
        }

        return new string(code);
    }

    private static string GetPrimaryMacAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up
                && network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .Select(network => network.GetPhysicalAddress().GetAddressBytes())
            .Where(address => address.Length == 6 && address.Any(value => value != 0))
            .Select(address => Convert.ToHexString(address))
            .OrderBy(address => address, StringComparer.Ordinal)
            .ToArray();

        return candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("未找到可用的有线或 Wi-Fi 网卡，无法生成设备识别码。");
    }

    private async Task PersistAsync(string code, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, code, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
