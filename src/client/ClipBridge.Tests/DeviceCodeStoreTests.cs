using ClipBridge.Sync;

namespace ClipBridge.Tests;

public sealed class DeviceCodeStoreTests
{
    [Fact]
    public void CodeFromMacAddressIsSixUppercaseAlphanumericCharacters()
    {
        var first = DeviceCodeStore.CreateCodeFromMacAddress("00-1a-2b-3c-4d-5e");
        var second = DeviceCodeStore.CreateCodeFromMacAddress("001A2B3C4D5E");

        Assert.Equal(first, second);
        Assert.Matches("^[A-Z0-9]{6}$", first);
    }

    [Fact]
    public void CodeFromMacAddressRejectsMalformedInput()
    {
        Assert.Throws<ArgumentException>(() => DeviceCodeStore.CreateCodeFromMacAddress("00-11-22"));
    }

    [Fact]
    public async Task GeneratedCodeIsPersistedAndCanBeChanged()
    {
        using var temporary = new DeviceCodeTempDirectory();
        var path = Path.Combine(temporary.Path, "device-code.txt");
        var store = new DeviceCodeStore(path);

        var first = await store.GetOrCreateAsync();
        var reopened = await new DeviceCodeStore(path).GetOrCreateAsync();
        await store.SetAsync("Q7W8E9");
        var changed = await store.GetOrCreateAsync();

        Assert.Equal(first, reopened);
        Assert.Equal("Q7W8E9", changed);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("A1B2C_")]
    public async Task SetRejectsInvalidDeviceCodes(string code)
    {
        using var temporary = new DeviceCodeTempDirectory();
        var store = new DeviceCodeStore(Path.Combine(temporary.Path, "device-code.txt"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(code));
    }
    private sealed class DeviceCodeTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipBridge-DeviceCode-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
