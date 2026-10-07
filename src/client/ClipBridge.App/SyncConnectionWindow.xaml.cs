using System.Net.Http;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace ClipBridge.App;

public partial class SyncConnectionWindow : Window
{
    private bool _setupSucceeded;
    private bool _hasSavedConnection;
    private string _initialDeviceCode = string.Empty;
    private string? _savedPeerDeviceCode;

    public SyncConnectionWindow()
    {
        InitializeComponent();
    }

    public void SetLocalDeviceCode(string code)
    {
        _initialDeviceCode = code;
        LocalDeviceCodeBox.Text = code;
    }

    public void SetSavedConnection(string? serverAddress, string? peerDeviceCode)
    {
        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            return;
        }

        _hasSavedConnection = true;
        _savedPeerDeviceCode = ClipBridge.Sync.DeviceCodeStore.IsValid(peerDeviceCode)
            ? peerDeviceCode!.Trim().ToUpperInvariant()
            : null;
        ServerAddressBox.Text = serverAddress;
        PeerDeviceCodeBox.Text = _savedPeerDeviceCode ?? string.Empty;
        StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        StatusText.Text = _savedPeerDeviceCode is null
            ? "已保存同步连接，应用启动后会自动恢复。填写对端识别码可发起配对或添加设备。"
            : "已保存同步连接和对端识别码，应用启动后会自动恢复；修改对端识别码可重新配对。";
        UpdateConnectButtonContent();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var currentPeerCode = PeerDeviceCodeBox.Text.Trim().ToUpperInvariant();
        var peerCodeUnchanged = string.IsNullOrEmpty(currentPeerCode)
            || string.Equals(currentPeerCode, _savedPeerDeviceCode, StringComparison.Ordinal);
        if (_setupSucceeded || (_hasSavedConnection && peerCodeUnchanged))
        {
            Close();
            return;
        }

        if (!Uri.TryCreate(ServerAddressBox.Text.Trim(), UriKind.Absolute, out var serverUri)
            || serverUri.Scheme is not ("http" or "https"))
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "请输入有效的 HTTP 或 HTTPS Server 地址。";
            return;
        }

        var localCode = LocalDeviceCodeBox.Text.Trim().ToUpperInvariant();
        if (!ClipBridge.Sync.DeviceCodeStore.IsValid(localCode))
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "本机识别码必须是 6 位大写字母或数字。";
            return;
        }

        ConnectButton.IsEnabled = false;
        LoadingOverlay.Visibility = Visibility.Visible;
        StatusText.Foreground = System.Windows.Media.Brushes.SlateGray;
        StatusText.Text = string.Empty;
        try
        {
            var syncClient = ((App)WpfApplication.Current).SyncClient;
            if (!string.Equals(localCode, _initialDeviceCode, StringComparison.Ordinal))
            {
                await syncClient.ConfigureDeviceCodeAsync(serverUri, localCode);
                _initialDeviceCode = localCode;
                ((App)WpfApplication.Current).LocalDeviceCode = localCode;
            }

            await syncClient.ActivateDeviceAsync(serverUri);

            var peerCode = PeerDeviceCodeBox.Text.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(peerCode))
            {
                await syncClient.RequestPairingAsync(serverUri, peerCode);
                _savedPeerDeviceCode = peerCode;
                _hasSavedConnection = true;
                StatusText.Text = "对端识别码已保存。若这是新设备，请在对端确认配对请求；已配对设备无需再次确认。";
            }
            else
            {
                StatusText.Text = "本机已登记并等待配对。把本机识别码提供给对端即可发起请求。";
            }

            _setupSucceeded = true;
            ConnectButton.Content = "完成";
            ConnectButton.IsDefault = false;
        }
        catch (HttpRequestException)
        {
            ShowFailure("无法连接 Server，请检查地址和网络。设备凭证不会明文保存在本机。" );
        }
        catch (InvalidOperationException exception)
        {
            ShowFailure(exception.Message);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("ClipBridge device pairing setup failed: {0}", exception);
            ShowFailure("登记或配对失败，请检查 Server 状态后重试。" );
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ConnectButton.IsEnabled = true;
        }
    }

    private void ShowFailure(string message)
    {
        StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        StatusText.Text = message;
    }

    private void PeerDeviceCodeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateConnectButtonContent();
    }

    private void UpdateConnectButtonContent()
    {
        if (_setupSucceeded)
        {
            return;
        }

        if (_hasSavedConnection)
        {
            var currentPeerCode = PeerDeviceCodeBox.Text.Trim().ToUpperInvariant();
            var peerCodeUnchanged = string.IsNullOrEmpty(currentPeerCode)
                || string.Equals(currentPeerCode, _savedPeerDeviceCode, StringComparison.Ordinal);
            ConnectButton.Content = peerCodeUnchanged ? "完成" : "请求配对";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
