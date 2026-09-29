using System.Windows;
using ClipBridge.Windows;

namespace ClipBridge.App;

public partial class MainWindow : Window
{
    private readonly ClipboardMonitor _clipboardMonitor;

    public MainWindow()
    {
        InitializeComponent();

        _clipboardMonitor = new ClipboardMonitor(this);
        _clipboardMonitor.TextCaptured += OnTextCaptured;
        _clipboardMonitor.ClipboardReadFailed += OnClipboardReadFailed;
        _clipboardMonitor.ListenerFailed += OnListenerFailed;
    }

    private void OnTextCaptured(string text)
    {
        var characterCount = new System.Globalization.StringInfo(text).LengthInTextElements;
        ClipboardStatusText.Text = $"最近检测到文本剪贴板更新（{characterCount} 个字符）。";
    }

    private void OnClipboardReadFailed() => ClipboardStatusText.Text = "暂时无法读取剪贴板，请重试。";

    private void OnListenerFailed(Exception _) => ClipboardStatusText.Text = "无法启动文本剪贴板监听。";
}
