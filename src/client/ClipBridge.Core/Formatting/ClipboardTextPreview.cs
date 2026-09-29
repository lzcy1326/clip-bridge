namespace ClipBridge.Core.Formatting;

public static class ClipboardTextPreview
{
    private const int VisibleLineCount = 2;

    public static string Create(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        if (lines.Length <= VisibleLineCount)
        {
            return text;
        }

        return string.Join(Environment.NewLine, lines.Take(VisibleLineCount)) + "…";
    }
}
