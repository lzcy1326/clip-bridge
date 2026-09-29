using System.Collections.ObjectModel;
using System.Windows;
using ClipBridge.Core.Interfaces;
using ClipBridge.Windows;

namespace ClipBridge.App;

public partial class MainWindow : Window
{
    private const int HistoryLimit = 1000;
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly IClipboardRepository _clipboardRepository;
    private readonly ObservableCollection<ClipBridge.Core.Models.ClipboardItem> _historyItems = [];

    public MainWindow()
    {
        InitializeComponent();

        HistoryList.ItemsSource = _historyItems;
        _clipboardRepository = ((App)System.Windows.Application.Current).ClipboardRepository;
        RefreshHistory();
        ClipboardStatusText.Text = _historyItems.Count == 0
            ? "等待复制文本。"
            : $"本地历史中已有 {_historyItems.Count} 条文本记录。";

        _clipboardMonitor = new ClipboardMonitor(this);
        _clipboardMonitor.TextCaptured += OnTextCaptured;
        _clipboardMonitor.ClipboardReadFailed += OnClipboardReadFailed;
        _clipboardMonitor.ListenerFailed += OnListenerFailed;
    }

    private void OnTextCaptured(string text)
    {
        _clipboardRepository.AddText(text, "local");
        RefreshHistory();
        ClipboardStatusText.Text = $"已保存到本地历史（{_historyItems.Count} 条文本记录）。";
    }

    private void RefreshHistory()
    {
        _historyItems.Clear();
        foreach (var item in _clipboardRepository.GetRecentTextItems(HistoryLimit))
        {
            _historyItems.Add(item);
        }

        HistoryCountText.Text = $"{_historyItems.Count} 条";
        EmptyHistoryText.Visibility = _historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClipboardReadFailed() => ClipboardStatusText.Text = "暂时无法读取剪贴板，请重试。";

    private void OnListenerFailed(Exception _) => ClipboardStatusText.Text = "无法启动文本剪贴板监听。";
}
