using System.Windows;
using ClipBridge.Core.Interfaces;
using ClipBridge.Windows;

namespace ClipBridge.App;

public partial class MainWindow : Window
{
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly IClipboardRepository _clipboardRepository;

    public MainWindow()
    {
        InitializeComponent();

        _clipboardRepository = ((App)System.Windows.Application.Current).ClipboardRepository;
        var savedTextCount = _clipboardRepository.GetTextCount();
        ClipboardStatusText.Text = savedTextCount == 0
            ? "等待复制文本。"
            : $"本地历史中已有 {savedTextCount} 条文本记录。";

        _clipboardMonitor = new ClipboardMonitor(this);
        _clipboardMonitor.TextCaptured += OnTextCaptured;
        _clipboardMonitor.ClipboardReadFailed += OnClipboardReadFailed;
        _clipboardMonitor.ListenerFailed += OnListenerFailed;
    }

    private void OnTextCaptured(string text)
    {
        _clipboardRepository.AddText(text, "local");
        var savedTextCount = _clipboardRepository.GetTextCount();
        ClipboardStatusText.Text = $"已保存到本地历史（{savedTextCount} 条文本记录）。";
    }

    private void OnClipboardReadFailed() => ClipboardStatusText.Text = "暂时无法读取剪贴板，请重试。";

    private void OnListenerFailed(Exception _) => ClipboardStatusText.Text = "无法启动文本剪贴板监听。";
}
