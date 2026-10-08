using System.Windows;
using ClipBridge.Sync;

namespace ClipBridge.App;

public partial class PairingRequestWindow : Window
{
    private readonly Func<bool, Task> _submitDecision;
    private readonly Action _dismissPrompt;
    private bool _submitting;
    private bool _decisionCompleted;

    public PairingRequestWindow(
        PendingPairingInfo request,
        Func<bool, Task> submitDecision,
        Action dismissPrompt)
    {
        ArgumentNullException.ThrowIfNull(request);
        _submitDecision = submitDecision ?? throw new ArgumentNullException(nameof(submitDecision));
        _dismissPrompt = dismissPrompt ?? throw new ArgumentNullException(nameof(dismissPrompt));
        InitializeComponent();

        RequesterText.Text = $"{request.RequesterName}（识别码 {request.RequesterCode}）";
        SourceText.Text = $"来源：{request.SourceAddress}\n确认后，两台设备将开始同步剪贴板。此请求于 {request.ExpiresAt.ToLocalTime():HH:mm:ss} 失效。";
    }

    private async void AcceptButton_Click(object sender, RoutedEventArgs e) => await SubmitDecisionAsync(accept: true);

    private async void RejectButton_Click(object sender, RoutedEventArgs e) => await SubmitDecisionAsync(accept: false);

    private async Task SubmitDecisionAsync(bool accept)
    {
        if (_submitting)
        {
            return;
        }

        _submitting = true;
        SetActionsEnabled(false);
        StatusText.Visibility = Visibility.Visible;
        StatusText.Foreground = System.Windows.Media.Brushes.SlateGray;
        StatusText.Text = "正在提交，请稍候…";

        try
        {
            await _submitDecision(accept);
            _decisionCompleted = true;
            Close();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("ClipBridge pairing decision failed: {0}", exception);
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "提交失败。请求可能已过期；请检查 Server 后重试，或让对端重新发起。";
            _submitting = false;
            SetActionsEnabled(true);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        RejectButton.IsEnabled = enabled;
        AcceptButton.IsEnabled = enabled;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_decisionCompleted)
        {
            _dismissPrompt();
        }

        base.OnClosed(e);
    }
}
