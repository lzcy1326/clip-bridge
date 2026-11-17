using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipBridge.App;

internal sealed class SyncShortcutWindow : Window
{
    private readonly RadioButton _automatic;
    private readonly RadioButton _manual;
    private readonly TextBox _shortcut;
    private readonly Func<SyncShortcutSettings, string?> _apply;
    private uint _modifiers;
    private uint _key;

    public SyncShortcutWindow(SyncShortcutSettings settings, Func<SyncShortcutSettings, string?> apply)
    {
        _apply = apply;
        _modifiers = settings.Modifiers;
        _key = settings.VirtualKey;
        Title = "同步快捷键";
        Width = 500;
        Height = 350;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = "选择同步方式", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        _automatic = new RadioButton { Content = "自动同步：普通复制和截图后自动发送", IsChecked = settings.Automatic, Margin = new Thickness(0, 0, 0, 10) };
        _manual = new RadioButton { Content = "手动同步：只在按下快捷键时发送", IsChecked = !settings.Automatic, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_automatic);
        panel.Children.Add(_manual);
        _shortcut = new TextBox { IsReadOnly = true, Height = 34, Padding = new Thickness(8, 5, 8, 5), Text = Describe(_modifiers, _key) };
        _shortcut.PreviewKeyDown += CaptureShortcut;
        panel.Children.Add(_shortcut);
        panel.Children.Add(new TextBlock { Text = "手动模式：选中内容按快捷键会复制并同步。截图后按 Ctrl+` 打开历史，选中图片并按同步快捷键发送。普通 Ctrl+C 只负责本机复制。文件暂不支持跨设备同步。", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 10, 0, 12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var save = new Button { Content = "保存", MinWidth = 80, Height = 32, IsDefault = true };
        var cancel = new Button { Content = "取消", MinWidth = 80, Height = 32, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            var error = _apply(new SyncShortcutSettings(_automatic.IsChecked == true, _modifiers, _key));
            if (error is not null) { MessageBox.Show(this, error, "ClipBridge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            DialogResult = true;
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
    }

    private void CaptureShortcut(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }
        var mods = Keyboard.Modifiers;
        _modifiers = (mods.HasFlag(ModifierKeys.Alt) ? 1u : 0u)
            | (mods.HasFlag(ModifierKeys.Control) ? 2u : 0u)
            | (mods.HasFlag(ModifierKeys.Shift) ? 4u : 0u)
            | (mods.HasFlag(ModifierKeys.Windows) ? 8u : 0u);
        _key = (uint)KeyInterop.VirtualKeyFromKey(key);
        _shortcut.Text = Describe(_modifiers, _key);
        _manual.IsChecked = true;
        e.Handled = true;
    }

    private static string Describe(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(KeyInterop.KeyFromVirtualKey((int)key).ToString());
        return string.Join(" + ", parts);
    }
}
