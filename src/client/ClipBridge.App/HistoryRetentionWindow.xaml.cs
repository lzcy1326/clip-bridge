using System.Globalization;
using System.Windows;

namespace ClipBridge.App;

public partial class HistoryRetentionWindow : Window
{
    private readonly string _settingsPath;

    public HistoryRetentionWindow(string settingsPath)
    {
        InitializeComponent();
        _settingsPath = settingsPath;
        var settings = HistoryRetentionSettings.Load(settingsPath);
        RetentionDaysBox.Text = settings.RetentionDays.ToString(CultureInfo.InvariantCulture);
        MaximumItemsBox.Text = settings.MaximumItems.ToString(CultureInfo.InvariantCulture);
        MaximumSizeBox.Text = (settings.MaximumContentBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionDaysBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days < 0
            || !int.TryParse(MaximumItemsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var maximumItems)
            || maximumItems < 0
            || !long.TryParse(MaximumSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var maximumMiB)
            || maximumMiB < 0
            || maximumMiB > long.MaxValue / (1024 * 1024))
        {
            StatusText.Text = "请输入不小于 0 的整数。";
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        try
        {
            new HistoryRetentionSettings(days, maximumItems, maximumMiB * 1024 * 1024).Save(_settingsPath);
            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"保存失败：{exception.Message}";
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }
}
