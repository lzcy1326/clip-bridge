using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using ClipBridge.Core.Diagnostics;
using ClipBridge.Core.Interfaces;
using ClipBridge.Core.Models;

namespace ClipBridge.Sync;

public sealed class ClipBridgeSyncClient : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
    private readonly HttpClient _httpClient = new();
    private readonly IClipboardRepository _repository;
    private readonly SyncProfileStore _profileStore;
    private readonly DeviceCodeStore _deviceCodeStore;
    private readonly DeviceSessionStore _deviceSessionStore;
    private readonly ConcurrentDictionary<string, byte> _shownPairingRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pullLock = new(1, 1);
    private readonly SemaphoreSlim _authenticationLock = new(1, 1);
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private Task? _imagePollTask;
    private Task? _notificationTask;
    private string? _accessToken;
    private bool _deviceAuthentication;
    private string? _sessionUsername;
    private string? _sessionPassword;
    private long _userId;
    private Uri? _serverUri;
    private Uri? _profileServerUri;
    private SyncProfile? _profile;
    private long _deviceId;
    private string? _peerDeviceCode;

    public ClipBridgeSyncClient(IClipboardRepository repository, string settingsPath)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _profileStore = new SyncProfileStore(settingsPath);
        _deviceCodeStore = new DeviceCodeStore(settingsPath + ".device-code");
        _deviceSessionStore = new DeviceSessionStore(settingsPath + ".device-session.json");
    }

    public event Action? HistoryUpdated;
    public event Action<string>? StatusChanged;
    public event Action<PendingPairingInfo>? PairingRequestReceived;

    public string? ConnectedServerAddress => IsConnected ? _serverUri!.GetLeftPart(UriPartial.Authority) : null;
    public string? PeerDeviceCode => _peerDeviceCode;

    public Task<string> GetLocalDeviceCodeAsync(CancellationToken cancellationToken = default) =>
        _deviceCodeStore.GetOrCreateAsync(cancellationToken);

    public bool IsConnected => _accessToken is not null && _serverUri is not null;

    public void ReportStatus(string status) => StatusChanged?.Invoke(status);

    public async Task ConnectAsync(Uri serverUri, string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (serverUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(serverUri.UserInfo))
        {
            throw new ArgumentException("The Server URL must use HTTP or HTTPS and cannot contain credentials.", nameof(serverUri));
        }

        await StopPollingAsync().ConfigureAwait(false);
        _accessToken = null;
        _deviceAuthentication = false;
        _sessionUsername = null;
        _sessionPassword = null;
        _userId = 0;
        _httpClient.DefaultRequestHeaders.Authorization = null;
        _profileServerUri = CreateServerBaseUri(serverUri);
        _serverUri = NormalizeServerBaseUri(serverUri);
        _profile = await _profileStore.LoadAsync(_profileServerUri, username, cancellationToken).ConfigureAwait(false);

        using var loginResponse = await _httpClient.PostAsJsonAsync(
            new Uri(_serverUri, "api/v1/auth/login"),
            new LoginRequest(username, password),
            cancellationToken).ConfigureAwait(false);
        if (loginResponse.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("账号或密码不正确，登录未成功。");
        }

        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Server returned an empty login response.");
        if (string.IsNullOrWhiteSpace(login.AccessToken) || login.User.Id <= 0)
        {
            throw new InvalidDataException("Server returned an invalid login response.");
        }

        _accessToken = login.AccessToken;
        _sessionUsername = username;
        _sessionPassword = password;
        _userId = login.User.Id;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (_profile.DeviceId <= 0)
        {
            _deviceId = await RegisterDeviceAsync(cancellationToken).ConfigureAwait(false);
            _profile = _profile with { DeviceId = _deviceId };
            await _profileStore.SaveAsync(_profileServerUri, username, _profile, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _deviceId = _profile.DeviceId;
        }

        StatusChanged?.Invoke($"已连接 {login.User.DisplayName} · {_serverUri.Host}");
        await PullAvailableHistoryAsync(cancellationToken).ConfigureAwait(false);
        _pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = PollLoopAsync(_pollCancellation.Token);
        _imagePollTask = ImagePollLoopAsync(_pollCancellation.Token, immediateFirstCycle: true);
        _notificationTask = NotificationLoopAsync(_pollCancellation.Token);
    }

    public async Task ActivateDeviceAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        await EnsureDeviceSessionAsync(serverUri, cancellationToken).ConfigureAwait(false);
        StartPolling(cancellationToken);
        await PullAvailableHistoryAsync(cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke("本机已登记，正在等待配对请求");
    }

    public async Task RestoreDeviceSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stageTimer = Stopwatch.StartNew();
            var session = await _deviceSessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            StartupPerformanceLog.Write("sync.session-load", stageTimer.Elapsed);
            if (session is null || !Uri.TryCreate(session.ServerAddress, UriKind.Absolute, out var serverUri))
            {
                StatusChanged?.Invoke("尚未配置设备同步");
                return;
            }

            stageTimer.Restart();
            await EnsureDeviceSessionAsync(serverUri, cancellationToken).ConfigureAwait(false);
            StartupPerformanceLog.Write("sync.profile-ready", stageTimer.Elapsed);
            StatusChanged?.Invoke($"正在恢复同步 · {_serverUri!.Host}");
            StartPolling(cancellationToken, immediateFirstCycle: true, reportRestoreSuccess: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown is expected to cancel startup restoration.
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("ClipBridge device session restore failed: {0}", exception);
            StatusChanged?.Invoke(exception is HttpRequestException or TaskCanceledException
                ? "Server 暂时不可用，设备同步会在恢复后重试"
                : "本机设备凭证无法读取，请重新连接并配对");
        }
    }

    public async Task RequestPairingAsync(Uri serverUri, string targetCode, CancellationToken cancellationToken = default)
    {
        targetCode = (targetCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!DeviceCodeStore.IsValid(targetCode))
        {
            throw new ArgumentException("对端识别码必须是 6 位大写字母或数字。", nameof(targetCode));
        }

        await EnsureDeviceSessionAsync(serverUri, cancellationToken).ConfigureAwait(false);
        var requesterCode = await _deviceCodeStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var request = new PairingRequest(requesterCode, targetCode);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_serverUri!, "api/v1/pairing-requests"))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.Equals(error?.Error, "already_paired", StringComparison.Ordinal))
            {
                await RememberPeerDeviceCodeAsync(targetCode, cancellationToken).ConfigureAwait(false);
                StartPolling(cancellationToken);
                StatusChanged?.Invoke("设备已配对，对端识别码已保存");
                return;
            }

            throw new InvalidOperationException("设备码冲突、设备已配对，或识别码需要重新生成。");
        }

        response.EnsureSuccessStatusCode();
        await RememberPeerDeviceCodeAsync(targetCode, cancellationToken).ConfigureAwait(false);
        StartPolling(cancellationToken);
        StatusChanged?.Invoke("配对请求已发送，请在对端确认");
    }

    public async Task DecidePairingRequestAsync(
        string requestId,
        bool accept,
        CancellationToken cancellationToken = default,
        string? peerDeviceCode = null)
    {
        if (!_deviceAuthentication || string.IsNullOrWhiteSpace(_accessToken) || _serverUri is null)
        {
            throw new InvalidOperationException("本机尚未登记到 Server。");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_serverUri, $"api/v1/pairing-requests/{Uri.EscapeDataString(requestId)}/decision"))
        {
            Content = JsonContent.Create(new PairingDecision(accept)),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            _shownPairingRequests.TryRemove(requestId, out _);
            throw;
        }

        StatusChanged?.Invoke(accept ? "设备配对成功" : "已拒绝配对请求");
        if (accept)
        {
            var normalizedPeerCode = (peerDeviceCode ?? string.Empty).Trim().ToUpperInvariant();
            if (DeviceCodeStore.IsValid(normalizedPeerCode))
            {
                await RememberPeerDeviceCodeAsync(normalizedPeerCode, cancellationToken).ConfigureAwait(false);
            }

            await PullAvailableHistoryAsync(cancellationToken).ConfigureAwait(false);
            await PullAvailableImagesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void DismissPairingPrompt(string requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            _shownPairingRequests.TryRemove(requestId, out _);
        }
    }

    public async Task ChangeDeviceCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!DeviceCodeStore.IsValid(code))
        {
            throw new ArgumentException("设备识别码必须是 6 位大写字母或数字。", nameof(code));
        }

        if (_deviceAuthentication && _serverUri is not null && !string.IsNullOrWhiteSpace(_accessToken))
        {
            using var message = new HttpRequestMessage(HttpMethod.Patch, new Uri(_serverUri, "api/v1/devices/me/code"))
            {
                Content = JsonContent.Create(new DeviceCodeRequest(code)),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new InvalidOperationException("该设备识别码已被使用，请换一个识别码。");
            }

            response.EnsureSuccessStatusCode();
            await _deviceCodeStore.SetAsync(code, cancellationToken).ConfigureAwait(false);
            var session = await _deviceSessionStore.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("未找到本机设备凭证。");
            await _deviceSessionStore.SaveAsync(session with { DeviceCode = code, PeerDeviceCode = null }, cancellationToken).ConfigureAwait(false);
            _peerDeviceCode = null;
        }
        else
        {
            await _deviceCodeStore.SetAsync(code, cancellationToken).ConfigureAwait(false);
        }

        StatusChanged?.Invoke("设备识别码已更新；原有配对已撤销，需要重新配对");
    }

    public async Task ConfigureDeviceCodeAsync(Uri serverUri, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        code = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!DeviceCodeStore.IsValid(code))
        {
            throw new ArgumentException("设备识别码必须是 6 位大写字母或数字。", nameof(code));
        }

        if (_deviceAuthentication && _serverUri is not null
            && string.Equals(_serverUri.GetLeftPart(UriPartial.Authority), serverUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            await ChangeDeviceCodeAsync(code, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _deviceCodeStore.SetAsync(code, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PublishTextAsync(ClipboardItem item, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || item.Type != ClipboardItemType.Text || item.TextContent is null || item.Remote)
        {
            return;
        }

        using var response = await SendAuthenticatedAsync(async (token, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_serverUri!, "api/v1/clips"))
            {
                Content = JsonContent.Create(new UploadRequest(item.Id, _deviceId, item.TextContent)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return;
        }

        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            StatusChanged?.Invoke("Server 暂时不可用");
            throw;
        }
        _repository.MarkSynced(item.Id);
        StatusChanged?.Invoke("已同步");
    }

    public async Task PublishImageAsync(ClipboardItem item, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || item.Type != ClipboardItemType.Image || item.Remote || item.Synced || string.IsNullOrWhiteSpace(item.ImagePath))
        {
            return;
        }

        var data = await File.ReadAllBytesAsync(item.ImagePath, cancellationToken).ConfigureAwait(false);
        if (data.Length is 0 or > 2 * 1024 * 1024)
        {
            StatusChanged?.Invoke("图片超过 2 MiB，未同步");
            return;
        }

        using var response = await SendAuthenticatedAsync(async (token, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_serverUri!, "api/v1/images"))
            {
                Content = new ByteArrayContent(data),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Clip-ID", item.Id);
            return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
        _repository.MarkImageSynced(item.Id);
        StatusChanged?.Invoke("截图已同步");
    }

    public async ValueTask DisposeAsync()
    {
        await StopPollingAsync().ConfigureAwait(false);
        _httpClient.Dispose();
        _pullLock.Dispose();
        _authenticationLock.Dispose();
    }

    private async Task<long> RegisterDeviceAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(_serverUri!, "api/v1/devices"),
            new DeviceRequest(Environment.MachineName, Environment.MachineName, "windows", "1.0.0"),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            MarkLoginExpired();
        }

        response.EnsureSuccessStatusCode();
        var device = await response.Content.ReadFromJsonAsync<DeviceResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Server returned an empty device response.");
        if (device.Id <= 0)
        {
            throw new InvalidDataException("Server returned an invalid device response.");
        }

        return device.Id;
    }

    private async Task PollLoopAsync(
        CancellationToken cancellationToken,
        bool immediateFirstCycle = false,
        bool reportRestoreSuccess = false)
    {
        using var timer = new PeriodicTimer(PollInterval);
        var runCycle = immediateFirstCycle;
        while (runCycle || await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            runCycle = false;
            try
            {
                var isRestoreCycle = reportRestoreSuccess;
                await PullAvailableHistoryAsync(cancellationToken, measurePerformance: isRestoreCycle).ConfigureAwait(false);
                if (reportRestoreSuccess)
                {
                    reportRestoreSuccess = false;
                    StatusChanged?.Invoke($"已恢复同步 · {_serverUri!.Host}");
                }

                await PollPendingPairingsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
                StatusChanged?.Invoke("Server 暂时不可用，正在重试");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                StatusChanged?.Invoke("Server 响应超时，正在重试");
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("ClipBridge history sync failed: {0}", exception);
                StatusChanged?.Invoke("同步暂时失败，正在重试");
            }
        }
    }

    private async Task PullAvailableHistoryAsync(CancellationToken cancellationToken, bool measurePerformance = false)
    {
        if (!IsConnected || _profile is null || _serverUri is null)
        {
            return;
        }

        await _pullLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pullTimer = Stopwatch.StartNew();
        var receivedCount = 0;
        try
        {
            var cursor = _profile.Cursor;
            var receivedAny = false;
            while (true)
            {
                var relativeUri = "api/v1/clips?limit=100" + (string.IsNullOrEmpty(cursor)
                    ? string.Empty
                    : "&after=" + Uri.EscapeDataString(cursor));
                var requestTimer = Stopwatch.StartNew();
                using var response = await SendAuthenticatedAsync(async (token, ct) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_serverUri, relativeUri));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                requestTimer.Stop();
                if (response is null)
                {
                    return;
                }

                response.EnsureSuccessStatusCode();
                var bodyTimer = Stopwatch.StartNew();
                var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                bodyTimer.Stop();
                var parseTimer = Stopwatch.StartNew();
                var page = JsonSerializer.Deserialize<HistoryPage>(responseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("Server returned an empty clipboard history page.");
                parseTimer.Stop();
                if (measurePerformance)
                {
                    var serverTiming = response.Headers.TryGetValues("Server-Timing", out var timingValues)
                        ? string.Join(",", timingValues)
                        : "unavailable";
                    StartupPerformanceLog.Write("sync.history-page-response-headers", requestTimer.Elapsed, serverTiming);
                    StartupPerformanceLog.Write("sync.history-page-body-download", bodyTimer.Elapsed, $"bytes={responseBody.Length.ToString(CultureInfo.InvariantCulture)}");
                    StartupPerformanceLog.Write("sync.history-page-parse", parseTimer.Elapsed, page.Items.Length);
                }

                var localItems = new List<ClipboardItem>(page.Items.Length);
                foreach (var remoteItem in page.Items)
                {
                    if (remoteItem.Type != "TEXT" || string.IsNullOrWhiteSpace(remoteItem.Text))
                    {
                        continue;
                    }

                    localItems.Add(new ClipboardItem(
                        remoteItem.Id,
                        remoteItem.OriginDeviceName,
                        ClipboardItemType.Text,
                        remoteItem.Text,
                        null,
                        remoteItem.ContentHash,
                        remoteItem.CreatedAt.ToUnixTimeMilliseconds(),
                        true,
                        remoteItem.OriginDeviceId != _deviceId));
                }

                if (localItems.Count > 0)
                {
                    var upsertTimer = Stopwatch.StartNew();
                    _repository.UpsertRemoteTexts(localItems, _deviceId.ToString(CultureInfo.InvariantCulture));
                    upsertTimer.Stop();
                    receivedAny = true;
                    receivedCount += localItems.Count;
                    if (measurePerformance)
                    {
                        StartupPerformanceLog.Write("sync.history-page-local-write", upsertTimer.Elapsed, localItems.Count);
                    }
                }

                if (!string.IsNullOrEmpty(page.NextCursor))
                {
                    cursor = page.NextCursor;
                    _profile = _profile with { Cursor = cursor };
                    var cursorTimer = Stopwatch.StartNew();
                    await _profileStore.SaveAsync(_profileServerUri!, _profile.Username, _profile, cancellationToken).ConfigureAwait(false);
                    if (measurePerformance)
                    {
                        StartupPerformanceLog.Write("sync.cursor-save", cursorTimer.Elapsed);
                    }
                }

                if (!page.HasMore)
                {
                    break;
                }

                if (string.IsNullOrEmpty(page.NextCursor))
                {
                    throw new InvalidDataException("Server returned another page without a cursor.");
                }
            }

            if (receivedAny)
            {
                HistoryUpdated?.Invoke();
            }
        }
        finally
        {
            _pullLock.Release();
            if (measurePerformance)
            {
                StartupPerformanceLog.Write("sync.history-catchup-total", pullTimer.Elapsed, receivedCount);
            }
        }
    }

    private async Task EnsureDeviceSessionAsync(Uri serverUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        if (serverUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(serverUri.UserInfo))
        {
            throw new ArgumentException("The Server URL must use HTTP or HTTPS and cannot contain credentials.", nameof(serverUri));
        }

        await StopPollingAsync().ConfigureAwait(false);
        _accessToken = null;
        _deviceAuthentication = false;
        _sessionUsername = null;
        _sessionPassword = null;
        _userId = 0;
        _httpClient.DefaultRequestHeaders.Authorization = null;
        var requestedProfileServerUri = CreateServerBaseUri(serverUri);
        _serverUri = NormalizeServerBaseUri(serverUri);

        var deviceCode = await _deviceCodeStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var profileKey = "device:" + deviceCode;
        var priorSession = await _deviceSessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        DeviceSession session;
        if (priorSession is not null
            && Uri.TryCreate(priorSession.ServerAddress, UriKind.Absolute, out var priorServerUri)
            && NormalizeServerBaseUri(priorServerUri) == _serverUri
            && string.Equals(priorSession.DeviceCode, deviceCode, StringComparison.Ordinal))
        {
            _profileServerUri = CreateServerBaseUri(priorServerUri);
            session = priorSession;
        }
        else
        {
            _profileServerUri = requestedProfileServerUri;
            using var response = await _httpClient.PostAsJsonAsync(
                new Uri(_serverUri, "api/v1/devices"),
                new DeviceRequest("ClipBridge", Environment.MachineName, "windows", "1.0.0", deviceCode),
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new InvalidOperationException("本机设备识别码已被占用，请先修改本机识别码后重试。");
            }

            response.EnsureSuccessStatusCode();
            var registered = await response.Content.ReadFromJsonAsync<DeviceBootstrapResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Server returned an empty device registration response.");
            if (registered.Device.Id <= 0 || string.IsNullOrWhiteSpace(registered.Credential))
            {
                throw new InvalidDataException("Server returned an invalid device credential.");
            }

            session = new DeviceSession(_profileServerUri.ToString(), deviceCode, registered.Device.Id, registered.Credential, null);
            await _deviceSessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }

        _deviceId = session.DeviceId;
        _accessToken = session.Credential;
        _deviceAuthentication = true;
        _peerDeviceCode = session.PeerDeviceCode;
        _profile = await _profileStore.LoadAsync(_profileServerUri, profileKey, cancellationToken).ConfigureAwait(false);
        if (_profile.DeviceId != _deviceId)
        {
            _profile = _profile with { DeviceId = _deviceId, Cursor = null };
            await _profileStore.SaveAsync(_profileServerUri, profileKey, _profile, cancellationToken).ConfigureAwait(false);
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private static Uri CreateServerBaseUri(Uri serverUri)
    {
        var builder = new UriBuilder(serverUri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static Uri NormalizeServerBaseUri(Uri serverUri)
    {
        var builder = new UriBuilder(CreateServerBaseUri(serverUri))
        {
            Host = string.Equals(serverUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                ? "127.0.0.1"
                : serverUri.Host,
        };
        return builder.Uri;
    }

    private async Task RememberPeerDeviceCodeAsync(string peerDeviceCode, CancellationToken cancellationToken)
    {
        var normalizedPeerCode = (peerDeviceCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!DeviceCodeStore.IsValid(normalizedPeerCode))
        {
            return;
        }

        var session = await _deviceSessionStore.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("未找到本机设备凭证，无法保存对端识别码。");
        await _deviceSessionStore.SaveAsync(session with { PeerDeviceCode = normalizedPeerCode }, cancellationToken).ConfigureAwait(false);
        _peerDeviceCode = normalizedPeerCode;
    }

    private void StartPolling(
        CancellationToken cancellationToken,
        bool immediateFirstCycle = false,
        bool reportRestoreSuccess = false)
    {
        if (_pollCancellation is not null)
        {
            return;
        }

        _pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollTask = PollLoopAsync(_pollCancellation.Token, immediateFirstCycle, reportRestoreSuccess);
        _imagePollTask = ImagePollLoopAsync(_pollCancellation.Token, immediateFirstCycle: true);
        _notificationTask = NotificationLoopAsync(_pollCancellation.Token);
    }

    private async Task NotificationLoopAsync(CancellationToken cancellationToken)
    {
        var retryAttempt = 0;
        while (!cancellationToken.IsCancellationRequested && IsConnected && _serverUri is not null)
        {
            var streamConnectedAt = DateTimeOffset.MinValue;
            try
            {
                using var response = await SendAuthenticatedAsync(async (token, ct) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_serverUri, "api/v1/events"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.ParseAdd("text/event-stream");
                    return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                if (response is null)
                {
                    return;
                }

                response.EnsureSuccessStatusCode();
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Server returned an invalid clipboard notification stream.");
                }

                streamConnectedAt = DateTimeOffset.UtcNow;
                // Subscribe first, then catch up by cursor so there is no gap between
                // history pull and event subscription. SSE is only a wakeup channel.
                await PullAvailableHistoryAsync(cancellationToken).ConfigureAwait(false);
                await PullAvailableImagesAsync(cancellationToken).ConfigureAwait(false);

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                string? eventName = null;
                var hasClipboardData = false;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        throw new IOException("Server closed the clipboard notification stream.");
                    }

                    if (line.Length == 0)
                    {
                        if (string.Equals(eventName, "clipboard", StringComparison.Ordinal) && hasClipboardData)
                        {
                            await PullAvailableHistoryAsync(cancellationToken).ConfigureAwait(false);
                            await PullAvailableImagesAsync(cancellationToken).ConfigureAwait(false);
                        }

                        eventName = null;
                        hasClipboardData = false;
                        continue;
                    }

                    if (line[0] == ':')
                    {
                        continue;
                    }

                    var separator = line.IndexOf(':');
                    var field = separator < 0 ? line : line[..separator];
                    var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');
                    if (field == "event")
                    {
                        eventName = value;
                    }
                    else if (field == "data" && value == "changed")
                    {
                        hasClipboardData = true;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested || !IsConnected)
                {
                    return;
                }

                if (streamConnectedAt != DateTimeOffset.MinValue
                    && DateTimeOffset.UtcNow - streamConnectedAt >= TimeSpan.FromSeconds(30))
                {
                    retryAttempt = 0;
                }
                Trace.TraceInformation("ClipBridge SSE notification stream disconnected: {0}", exception.Message);
            }
            catch (Exception exception)
            {
                if (streamConnectedAt != DateTimeOffset.MinValue
                    && DateTimeOffset.UtcNow - streamConnectedAt >= TimeSpan.FromSeconds(30))
                {
                    retryAttempt = 0;
                }
                Trace.TraceError("ClipBridge SSE notification stream failed: {0}", exception);
            }

            retryAttempt = Math.Min(retryAttempt + 1, 5);
            var delaySeconds = Math.Min(30, 1 << (retryAttempt - 1));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 251)), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task PollPendingPairingsAsync(CancellationToken cancellationToken)
    {
        if (!_deviceAuthentication || string.IsNullOrWhiteSpace(_accessToken) || _serverUri is null)
        {
            return;
        }

        using var response = await SendAuthenticatedAsync(async (token, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_serverUri, "api/v1/pairing-requests/pending"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<PendingPairingEnvelope>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Server returned an empty pending pairing response.");
        foreach (var pending in envelope.Items)
        {
            if (!string.IsNullOrWhiteSpace(pending.Id) && _shownPairingRequests.TryAdd(pending.Id, 0))
            {
                PairingRequestReceived?.Invoke(pending);
            }
        }
    }

    private void MarkLoginExpired()
    {
        _accessToken = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
        StatusChanged?.Invoke(_deviceAuthentication ? "设备凭证已失效，请重新登记并配对" : "登录已过期，请重新连接");
        _deviceAuthentication = false;
        _pollCancellation?.Cancel();
    }

    private async Task<HttpResponseMessage?> SendAuthenticatedAsync(
        Func<string, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        var rejectedToken = _accessToken;
        if (string.IsNullOrEmpty(rejectedToken))
        {
            return null;
        }

        var response = await send(rejectedToken, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        if (_deviceAuthentication)
        {
            MarkLoginExpired();
            return null;
        }

        if (!await TryReauthenticateAsync(rejectedToken, cancellationToken).ConfigureAwait(false))
        {
            MarkLoginExpired();
            return null;
        }

        response = await send(_accessToken!, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            MarkLoginExpired();
            return null;
        }

        return response;
    }

    private async Task ImagePollLoopAsync(CancellationToken cancellationToken, bool immediateFirstCycle)
    {
        using var timer = new PeriodicTimer(PollInterval);
        var runCycle = immediateFirstCycle;
        while (runCycle || await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            runCycle = false;
            try
            {
                foreach (var pendingImage in _repository.GetPendingImages(20))
                {
                    await PublishImageAsync(pendingImage, cancellationToken).ConfigureAwait(false);
                }

                await PullAvailableImagesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
                // Image transfer retries independently and never interrupts text sync.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The next cycle retries timed-out image requests.
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("ClipBridge image sync failed: {0}", exception);
            }
        }
    }

    private async Task PullAvailableImagesAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || _profile is null || _serverUri is null)
        {
            return;
        }

        await _pullLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var receivedAny = false;
        try
        {
            var cursor = _profile.ImageCursor;
            while (true)
            {
                var relativeUri = "api/v1/images?limit=100" + (string.IsNullOrEmpty(cursor)
                    ? string.Empty
                    : "&after=" + Uri.EscapeDataString(cursor));
                using var response = await SendAuthenticatedAsync(async (token, ct) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_serverUri, relativeUri));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                if (response is null)
                {
                    return;
                }

                response.EnsureSuccessStatusCode();
                var page = await response.Content.ReadFromJsonAsync<ImageHistoryPage>(cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Server returned an empty image history page.");
                foreach (var image in page.Items)
                {
                    if (image.Type != "IMAGE" || image.OriginDeviceId == _deviceId || string.IsNullOrWhiteSpace(image.Id)
                        || !string.Equals(image.ContentType, "image/png", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var imageResponse = await SendAuthenticatedAsync(async (token, ct) =>
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_serverUri, "api/v1/images/" + Uri.EscapeDataString(image.Id)));
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);
                    if (imageResponse is null)
                    {
                        return;
                    }

                    imageResponse.EnsureSuccessStatusCode();
                    var bytes = await ReadBoundedImageAsync(imageResponse.Content, cancellationToken).ConfigureAwait(false);
                    _repository.UpsertRemoteImage(image.Id, image.OriginDeviceName, bytes, image.CreatedAt.ToUnixTimeMilliseconds());
                    receivedAny = true;
                }

                if (!string.IsNullOrWhiteSpace(page.NextCursor))
                {
                    cursor = page.NextCursor;
                    _profile = _profile with { ImageCursor = cursor };
                    await _profileStore.SaveAsync(_profileServerUri!, _profile.Username, _profile, cancellationToken).ConfigureAwait(false);
                }

                if (!page.HasMore)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(page.NextCursor))
                {
                    throw new InvalidDataException("Server returned another image page without a cursor.");
                }
            }
        }
        finally
        {
            _pullLock.Release();
        }

        if (receivedAny)
        {
            HistoryUpdated?.Invoke();
        }
    }

    private static async Task<byte[]> ReadBoundedImageAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 2 * 1024 * 1024;
        if (content.Headers.ContentLength is > maximumBytes)
        {
            throw new InvalidDataException("Server image exceeds the 2 MiB limit.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Server image exceeds the 2 MiB limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task<bool> TryReauthenticateAsync(string rejectedToken, CancellationToken cancellationToken)
    {
        await _authenticationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_accessToken, rejectedToken, StringComparison.Ordinal))
            {
                return !string.IsNullOrEmpty(_accessToken);
            }

            if (_serverUri is null || string.IsNullOrWhiteSpace(_sessionUsername) || string.IsNullOrEmpty(_sessionPassword))
            {
                return false;
            }

            using var response = await _httpClient.PostAsJsonAsync(
                new Uri(_serverUri, "api/v1/auth/login"),
                new LoginRequest(_sessionUsername, _sessionPassword),
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Server returned an empty login response during session recovery.");
            if (string.IsNullOrWhiteSpace(login.AccessToken) || login.User.Id != _userId)
            {
                return false;
            }

            _accessToken = login.AccessToken;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            StatusChanged?.Invoke("同步连接已恢复");
            return true;
        }
        finally
        {
            _authenticationLock.Release();
        }
    }

    private async Task StopPollingAsync()
    {
        if (_pollCancellation is null)
        {
            return;
        }

        await _pollCancellation.CancelAsync().ConfigureAwait(false);
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_imagePollTask is not null)
        {
            try
            {
                await _imagePollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_notificationTask is not null)
        {
            try
            {
                await _notificationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pollCancellation.Dispose();
        _pollCancellation = null;
        _pollTask = null;
        _imagePollTask = null;
        _notificationTask = null;
    }

    private sealed record LoginRequest([property: JsonPropertyName("username")] string Username, [property: JsonPropertyName("password")] string Password);
    private sealed record UploadRequest([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("device_id")] long DeviceId, [property: JsonPropertyName("text")] string Text);
    private sealed record DeviceRequest([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("machine_name")] string MachineName, [property: JsonPropertyName("platform")] string Platform, [property: JsonPropertyName("client_version")] string ClientVersion, [property: JsonPropertyName("device_code")] string DeviceCode = "");
    private sealed record PairingRequest([property: JsonPropertyName("requester_code")] string RequesterCode, [property: JsonPropertyName("target_code")] string TargetCode);
    private sealed record PairingDecision([property: JsonPropertyName("accept")] bool Accept);
    private sealed record ApiErrorResponse([property: JsonPropertyName("error")] string Error);
    private sealed record DeviceCodeRequest([property: JsonPropertyName("device_code")] string DeviceCode);
    private sealed record LoginResponse([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("user")] UserResponse User);
    private sealed record UserResponse([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("display_name")] string DisplayName);
    private sealed record DeviceResponse([property: JsonPropertyName("id")] long Id);
    private sealed record DeviceBootstrapResponse([property: JsonPropertyName("device")] DeviceResponse Device, [property: JsonPropertyName("credential")] string Credential);
    private sealed record PendingPairingEnvelope([property: JsonPropertyName("items")] PendingPairingInfo[] Items);
    private sealed record HistoryPage([property: JsonPropertyName("items")] HistoryItem[] Items, [property: JsonPropertyName("next_cursor")] string? NextCursor, [property: JsonPropertyName("has_more")] bool HasMore);
    private sealed record HistoryItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("origin_device_id")] long OriginDeviceId,
        [property: JsonPropertyName("origin_device_name")] string OriginDeviceName,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("content_hash")] string ContentHash,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
}

public sealed record PendingPairingInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("requester_name")] string RequesterName,
    [property: JsonPropertyName("requester_code")] string RequesterCode,
    [property: JsonPropertyName("source_address")] string SourceAddress,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

internal sealed class SyncProfileStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public SyncProfileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<SyncProfile> LoadAsync(Uri serverUri, string username, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var key = MakeKey(serverUri, username);
            return profiles.TryGetValue(key, out var profile)
                ? profile with { Username = username }
                : new SyncProfile(username, 0, null);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(Uri serverUri, string username, SyncProfile profile, CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = await ReadAsync(cancellationToken).ConfigureAwait(false);
            profiles[MakeKey(serverUri, username)] = profile with { Username = username };
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(profiles), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<Dictionary<string, SyncProfile>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, SyncProfile>(StringComparer.Ordinal);
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, SyncProfile>>(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? new Dictionary<string, SyncProfile>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, SyncProfile>(StringComparer.Ordinal);
        }
    }

    private static string MakeKey(Uri serverUri, string username) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(serverUri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + "\n" + username.Trim().ToLowerInvariant())));
}

internal sealed record SyncProfile(string Username, long DeviceId, string? Cursor, string? ImageCursor = null);

internal sealed record ImageHistoryPage(
    [property: JsonPropertyName("items")] ImageHistoryItem[] Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore);

internal sealed record ImageHistoryItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("origin_device_id")] long OriginDeviceId,
    [property: JsonPropertyName("origin_device_name")] string OriginDeviceName,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("byte_size")] long ByteSize,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
