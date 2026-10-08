using ClipBridge.Core.Formatting;

namespace ClipBridge.Tests;

public sealed class ClipboardTextPreviewTests
{
    [Theory]
    [InlineData("first\nsecond\nthird")]
    [InlineData("first\r\nsecond\r\nthird")]
    [InlineData("first\rsecond\rthird")]
    public void Create_TruncatesAfterTwoLines(string text)
    {
        Assert.Equal($"first{Environment.NewLine}second…", ClipboardTextPreview.Create(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("one line")]
    [InlineData("first\nsecond")]
    public void Create_PreservesTextWithAtMostTwoLines(string? text)
    {
        Assert.Equal(text ?? string.Empty, ClipboardTextPreview.Create(text));
    }
}
