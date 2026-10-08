using System.Globalization;
using System.Windows.Data;
using ClipBridge.Core.Formatting;

namespace ClipBridge.App;

public sealed class HistoryPreviewTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return ClipboardTextPreview.Create(value as string);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
