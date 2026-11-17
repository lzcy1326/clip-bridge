using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClipBridge.Core.Models;
using ClipBridge.Storage.Repositories;
using ClipBridge.Sync;
using Microsoft.Data.Sqlite;

namespace ClipBridge.Tests;

public sealed class ClipBridgeSyncClientTests
{
    [Fact]
    public async Task ManualModeDoesNotUploadOrdinaryCopiesButCanPublishExplicitly()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.DisableNotifications();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipboard.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "profiles.json"));
        client.SetAutomaticUploadEnabled(false);
        await client.ConnectAsync(server.BaseUri, "test-user", "test-password");
        var local = repository.AddText("private local copy", "local")!;
        var localImage = repository.AddImage(new byte[] { 1, 2, 3 }, "local");
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(repository.FindById(local.Id)!.Synced);
        Assert.False(repository.FindById(localImage.Id)!.Synced);
        Assert.Null(server.UploadedText);
        Assert.Equal(0, server.ImageUploadAttempts);

        server.AddRemoteText("manual-mode-remote", 89, "VM-TEST", "still receiving", "manual-mode-hash");
        await WaitUntilAsync(() => repository.FindById("manual-mode-remote") is not null, TimeSpan.FromSeconds(10));
        await client.PublishTextAsync(local);
        Assert.True(repository.FindById(local.Id)!.Synced);
        Assert.Equal("private local copy", server.UploadedText);
        await client.PublishImageAsync(localImage);
        Assert.True(repository.FindById(localImage.Id)!.Synced);
        Assert.Equal(1, server.ImageUploadAttempts);

        var notRequested = repository.AddText("do not send when switching modes", "local")!;
        await Task.Delay(30);
        client.SetAutomaticUploadEnabled(true);
        var automatic = repository.AddText("new automatic copy", "local")!;
        await WaitUntilAsync(() => repository.FindById(automatic.Id)?.Synced == true, TimeSpan.FromSeconds(10));
        Assert.False(repository.FindById(notRequested.Id)!.Synced);
    }

    [Fact]
    public async Task OwnServerEchoIsIgnoredAndRemoteCopyIsDelivered()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.DisableNotifications();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipboard.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "profiles.json"));
        var received = new ConcurrentQueue<ClipboardItem>();
        client.RemoteClipboardReceived += received.Enqueue;
        await client.ActivateDeviceAsync(server.BaseUri);
        var local = repository.AddText("locally copied", "HOST-PC")!;
        Assert.True(repository.MarkSynced(local.Id));
        server.AddRemoteText(local.Id, 77, "HOST-PC", local.TextContent!, local.ContentHash);
        await WaitUntilAsync(() => server.HistoryRequests.Count >= 2, TimeSpan.FromSeconds(10));
        server.AddRemoteText("another-device-copy", 89, "VM-PC", "new remote value", "remote-hash");
        await WaitUntilAsync(() => !received.IsEmpty, TimeSpan.FromSeconds(10));
        Assert.False(repository.FindById(local.Id)!.Remote);
        Assert.Equal("new remote value", Assert.Single(received).TextContent);
    }

    [Fact]
    public async Task ReconnectionSkipsOfflineCopiesAndContinuesWithNewCopies()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.DisableNotifications();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipboard.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "profiles.json"));
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += status => { if (status.Contains("HTTP 503")) unavailable.TrySetResult(); };
        await client.ConnectAsync(server.BaseUri, "test-user", "test-password");
        server.FailNextHistoryRequest();
        await unavailable.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var offline = repository.AddText("copied while offline", "local")!;
        await WaitUntilAsync(() => server.HealthRequests >= 2 && server.HistoryRequests.Count >= 2, TimeSpan.FromSeconds(10));
        var live = repository.AddText("copied after recovery", "local")!;
        await WaitUntilAsync(() => repository.FindById(live.Id)?.Synced == true, TimeSpan.FromSeconds(10));
        Assert.False(repository.FindById(offline.Id)!.Synced);
        Assert.Equal("copied after recovery", server.UploadedText);
    }

    [Fact]
    public async Task ConnectionStartsTextAndImageCursorsAtCurrentTime()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.DisableNotifications();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipboard.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "profiles.json"));
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await client.ActivateDeviceAsync(server.BaseUri);
        await WaitUntilAsync(() => !server.ImageRequests.IsEmpty && !server.HistoryRequests.IsEmpty, TimeSpan.FromSeconds(5));
        foreach (var request in new[] { server.HistoryRequests.First(), server.ImageRequests.First() })
        {
            var token = Uri.UnescapeDataString(request.Split("after=", 2)[1].Split('&')[0]);
            var encoded = token.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            using var cursor = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var timestamp = cursor.RootElement.GetProperty("created_at").GetDateTimeOffset();
            Assert.InRange(timestamp, before, DateTimeOffset.UtcNow.AddSeconds(1));
        }
    }

    [Fact]
    public async Task RejectedDeviceTextUploadDoesNotBlockRemoteHistoryPolling()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.DisableNotifications();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipboard.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "profiles.json"));
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += status => { if (status.Contains("HTTP 404")) rejected.TrySetResult(); };
        await client.ActivateDeviceAsync(server.BaseUri);
        var local = repository.AddText("keep pending after rejection", "local")!;
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        server.AddRemoteText("remote-after-rejection", 89, "VM-TEST", "still receiving", "remote-after-rejection-hash");
        await WaitUntilAsync(() => repository.FindById("remote-after-rejection") is not null, TimeSpan.FromSeconds(10));
        Assert.False(repository.FindById(local.Id)!.Synced);
    }

    [Fact]
    public async Task UnresponsiveServerDoesNotDelayLocalCaptureHistoryOrClipboardWrite()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.DisableNotifications();
        server.DelayNextHistoryResponse(TimeSpan.FromSeconds(30));
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipboard.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "profiles.json"));
        using var stop = new CancellationTokenSource();
        var connection = Task.Run(() => client.ConnectAsync(server.BaseUri, "test-user", "test-password", stop.Token));
        try
        {
            await WaitUntilAsync(() => server.HistoryRequests.Count > 0, TimeSpan.FromSeconds(5));
            Assert.False(connection.IsCompleted);
            await using var history = new ClipBridge.Core.Services.LocalClipboardHistory(repository);
            var timer = Stopwatch.StartNew();
            history.CaptureText("available offline", "local");
            history.CaptureFiles([@"C:\offline.txt"], "local");
            Assert.Contains(history.Snapshot(), entry => entry.Item.TextContent == "available offline");
            string? copied = null;
            using var writer = new ClipBridge.Windows.ClipboardWriter(setText: text => copied = text);
            await writer.SetTextAsync("available offline").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("available offline", copied);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
            Assert.False(connection.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection);
        }
    }

    [Fact]
    public async Task PollingUploadsLocallyPersistedTextWithoutTheUiWaitingForTheServer()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.DisableNotifications();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipboard.db"));
        var text = repository.AddText("saved while offline", "local")!;
        repository.AddFiles([@"C:\private-local-file.txt"], "local");
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "profiles.json"));
        await client.ConnectAsync(server.BaseUri, "test-user", "test-password");
        var current = repository.AddText("copied after connection", "local")!;
        await WaitUntilAsync(() => repository.FindById(current.Id)?.Synced == true, TimeSpan.FromSeconds(10));
        Assert.Equal("copied after connection", server.UploadedText);
        Assert.False(repository.FindById(text.Id)!.Synced);
    }

    [Fact]
    public async Task ReauthenticatesAfterUnauthorizedHistoryAndPublishRequests()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "sync-profiles.json"));
        var localhostServerUri = new UriBuilder(server.BaseUri) { Host = "localhost" }.Uri;

        await client.ConnectAsync(localhostServerUri, "test-user", "never-save-this-password");

        Assert.Equal(server.BaseUri.GetLeftPart(UriPartial.Authority), client.ConnectedServerAddress);
        Assert.Equal(1, server.LoginCount);
        Assert.Empty(repository.GetRecentTextItems(10));

        server.ExpireNextAuthorizedRequest();
        var local = repository.AddText("from-client", "LOCAL-PC")!;
        await client.PublishTextAsync(local);

        Assert.Equal(2, server.LoginCount);
        Assert.Equal(local.Id, server.UploadedId);
        Assert.True(repository.FindById(local.Id)!.Synced);
        var savedProfiles = await File.ReadAllTextAsync(Path.Combine(temporary.Path, "sync-profiles.json"));
        Assert.DoesNotContain("never-save-this-password", savedProfiles, StringComparison.Ordinal);
        Assert.DoesNotContain("test-access-token", savedProfiles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollingRecoversAfterAnExpiredAccessToken()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "sync-profiles.json"));
        await client.ConnectAsync(server.BaseUri, "test-user", "never-save-this-password");

        server.ExpireNextAuthorizedRequest();
        await WaitUntilAsync(
            () => server.LoginCount == 2 && server.HistoryRequests.Count >= 2,
            TimeSpan.FromSeconds(10));

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task PollingFindsNewRemoteTextAndRaisesHistoryUpdated()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "sync-profiles.json"));

        var historyUpdated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HistoryUpdated += () => historyUpdated.TrySetResult(true);
        server.DisableNotifications();
        await client.ActivateDeviceAsync(server.BaseUri);
        server.AddRemoteText("remote-clip-2", 89, "VM-TEST", "polling-delivery", "polling-hash");

        await historyUpdated.Task.WaitAsync(TimeSpan.FromSeconds(8));

        var received = repository.FindById("remote-clip-2");
        Assert.NotNull(received);
        Assert.Equal("polling-delivery", received.TextContent);
        Assert.Equal("VM-TEST", received.OriginDeviceId);
        Assert.True(received.Remote);
        Assert.True(received.Synced);
        Assert.True(server.HistoryRequests.Count >= 2, "Expected the follow-up short-interval HTTP history poll.");
    }

    [Fact]
    public async Task SseNotificationPullsRemoteTextBeforeFourSecondFallback()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "sync-profiles.json"));
        var historyUpdated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HistoryUpdated += () => historyUpdated.TrySetResult(true);

        await client.ActivateDeviceAsync(server.BaseUri);
        await server.NotificationConnection.Task.WaitAsync(TimeSpan.FromSeconds(2));
        server.AddRemoteText("remote-clip-sse", 89, "VM-TEST", "sse-delivery", "sse-hash");

        await historyUpdated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("sse-delivery", repository.FindById("remote-clip-sse")?.TextContent);
    }

    [Fact]
    public async Task RegistersDeviceAnonymouslyAndRequestsPairingWithDiscoveryCode()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var settingsPath = Path.Combine(temporary.Path, "sync-profiles.json");
        await using var client = new ClipBridgeSyncClient(repository, settingsPath);

        await client.ActivateDeviceAsync(server.BaseUri);
        await client.RequestPairingAsync(server.BaseUri, "PEER12");
        await Task.Delay(50);

        Assert.Equal(1, server.AnonymousDeviceRegistrationCount);
        Assert.True(ClipBridge.Sync.DeviceCodeStore.IsValid(server.RegisteredDeviceCode));
        Assert.Equal(server.RegisteredDeviceCode, server.PairingRequesterCode);
        Assert.Equal("PEER12", server.PairingTargetCode);
        var session = await File.ReadAllTextAsync(settingsPath + ".device-session.json");
        Assert.DoesNotContain("test-device-credential", session, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingServerAddressPreservesPairedDeviceSession()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var settingsPath = Path.Combine(temporary.Path, "sync-profiles.json");
        var newAddress = new UriBuilder(server.BaseUri) { Host = "localhost" }.Uri;

        await using (var client = new ClipBridgeSyncClient(repository, settingsPath))
        {
            await client.ActivateDeviceAsync(server.BaseUri);
            await client.RequestPairingAsync(server.BaseUri, "PEER12");
            await client.ActivateDeviceAsync(newAddress);
            Assert.Equal("PEER12", client.PeerDeviceCode);
        }

        var session = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath + ".device-session.json"));
        Assert.Equal(newAddress.ToString(), session?["ServerAddress"]?.GetValue<string>());
        await using var restartedClient = new ClipBridgeSyncClient(repository, settingsPath);
        await restartedClient.RestoreDeviceSessionAsync();
        Assert.True(restartedClient.IsConnected);
        Assert.Equal("PEER12", restartedClient.PeerDeviceCode);
        Assert.Equal(1, server.AnonymousDeviceRegistrationCount);
    }

    [Fact]
    public async Task RestoresSavedDeviceSessionAfterRestartWithoutPairingAgain()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var settingsPath = Path.Combine(temporary.Path, "sync-profiles.json");
        var localhostServerUri = new UriBuilder(server.BaseUri) { Host = "localhost" }.Uri;

        await using (var firstClient = new ClipBridgeSyncClient(repository, settingsPath))
        {
            await firstClient.ActivateDeviceAsync(localhostServerUri);
            await firstClient.RequestPairingAsync(server.BaseUri, "PEER12");
        }

        var sessionPath = settingsPath + ".device-session.json";
        var savedSession = JsonNode.Parse(await File.ReadAllTextAsync(sessionPath))!.AsObject();
        savedSession["ServerAddress"] = localhostServerUri.ToString();
        await File.WriteAllTextAsync(sessionPath, savedSession.ToJsonString());

        var restoredStatuses = new ConcurrentQueue<string>();
        await using (var restartedClient = new ClipBridgeSyncClient(repository, settingsPath))
        {
            restartedClient.StatusChanged += restoredStatuses.Enqueue;
            await restartedClient.RestoreDeviceSessionAsync();

            Assert.True(restartedClient.IsConnected);
            Assert.Equal(server.BaseUri.GetLeftPart(UriPartial.Authority), restartedClient.ConnectedServerAddress);
            Assert.Contains(restoredStatuses, status => status.StartsWith("正在恢复同步", StringComparison.Ordinal));
            await WaitUntilAsync(
                () => restoredStatuses.Any(status => status.StartsWith("已恢复同步", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, server.AnonymousDeviceRegistrationCount);
        Assert.Equal(1, server.PairingRequestCount);
        Assert.Equal("PEER12", server.PairingTargetCode);
        Assert.True(server.HistoryRequests.Count >= 2);
        var normalizedSession = JsonNode.Parse(await File.ReadAllTextAsync(sessionPath));
        Assert.Equal(localhostServerUri.ToString(), normalizedSession?["ServerAddress"]?.GetValue<string>());
    }

    [Fact]
    public async Task StartsSessionRestoreBeforeTheHistoryRequestCompletes()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var settingsPath = Path.Combine(temporary.Path, "sync-profiles.json");

        await using (var firstClient = new ClipBridgeSyncClient(repository, settingsPath))
        {
            await firstClient.ActivateDeviceAsync(server.BaseUri);
        }

        server.DelayNextHistoryResponse(TimeSpan.FromSeconds(2));
        var statuses = new ConcurrentQueue<string>();
        await using var restartedClient = new ClipBridgeSyncClient(repository, settingsPath);
        restartedClient.StatusChanged += statuses.Enqueue;
        var stopwatch = Stopwatch.StartNew();

        await restartedClient.RestoreDeviceSessionAsync();

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Restore blocked for {stopwatch.Elapsed}.");
        Assert.Contains(statuses, status => status.StartsWith("正在恢复同步", StringComparison.Ordinal));
        await WaitUntilAsync(
            () => statuses.Any(status => status.StartsWith("已恢复同步", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RestoresRememberedPeerCodeAfterInitiatingPairing()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var settingsPath = Path.Combine(temporary.Path, "sync-profiles.json");

        await using (var firstClient = new ClipBridgeSyncClient(repository, settingsPath))
        {
            await firstClient.ActivateDeviceAsync(server.BaseUri);
            await firstClient.RequestPairingAsync(server.BaseUri, "PEER12");
            Assert.Equal("PEER12", firstClient.PeerDeviceCode);
        }

        await using var restartedClient = new ClipBridgeSyncClient(repository, settingsPath);
        await restartedClient.RestoreDeviceSessionAsync();

        Assert.Equal("PEER12", restartedClient.PeerDeviceCode);
        var session = await File.ReadAllTextAsync(settingsPath + ".device-session.json");
        Assert.Contains("PEER12", session, StringComparison.Ordinal);
        Assert.DoesNotContain("test-device-credential-secret", session, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavesPeerCodeWhenServerReportsDevicesAreAlreadyPaired()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.ReturnAlreadyPaired();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var settingsPath = Path.Combine(temporary.Path, "sync-profiles.json");

        await using (var client = new ClipBridgeSyncClient(repository, settingsPath))
        {
            await client.ActivateDeviceAsync(server.BaseUri);
            await client.RequestPairingAsync(server.BaseUri, "PEER12");
            Assert.Equal("PEER12", client.PeerDeviceCode);
        }

        await using var restartedClient = new ClipBridgeSyncClient(repository, settingsPath);
        await restartedClient.RestoreDeviceSessionAsync();

        Assert.Equal("PEER12", restartedClient.PeerDeviceCode);
        Assert.Equal(1, server.PairingRequestCount);
    }

    [Fact]
    public async Task SavesRequesterCodeWhenPairingIsAcceptedAndRestoresIt()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.ReturnPendingPairing();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var settingsPath = Path.Combine(temporary.Path, "sync-profiles.json");

        await using (var receiver = new ClipBridgeSyncClient(repository, settingsPath))
        {
            var requests = new ConcurrentQueue<PendingPairingInfo>();
            receiver.PairingRequestReceived += requests.Enqueue;
            await receiver.ActivateDeviceAsync(server.BaseUri);
            await WaitUntilAsync(() => requests.Count == 1, TimeSpan.FromSeconds(10));

            var request = Assert.Single(requests);
            await receiver.DecidePairingRequestAsync(request.Id, accept: true, peerDeviceCode: request.RequesterCode);
            Assert.Equal("PEER12", receiver.PeerDeviceCode);
        }

        await using var restartedReceiver = new ClipBridgeSyncClient(repository, settingsPath);
        await restartedReceiver.RestoreDeviceSessionAsync();

        Assert.Equal("PEER12", restartedReceiver.PeerDeviceCode);
    }

    [Fact]
    public async Task RestoresLegacyDeviceSessionWithoutPeerCode()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var settingsPath = Path.Combine(temporary.Path, "sync-profiles.json");
        var sessionPath = settingsPath + ".device-session.json";

        await using (var firstClient = new ClipBridgeSyncClient(repository, settingsPath))
        {
            await firstClient.ActivateDeviceAsync(server.BaseUri);
        }

        var legacySession = JsonNode.Parse(await File.ReadAllTextAsync(sessionPath))!.AsObject();
        legacySession.Remove("PeerDeviceCode");
        await File.WriteAllTextAsync(sessionPath, legacySession.ToJsonString());

        await using var restartedClient = new ClipBridgeSyncClient(repository, settingsPath);
        await restartedClient.RestoreDeviceSessionAsync();

        Assert.True(restartedClient.IsConnected);
        Assert.Null(restartedClient.PeerDeviceCode);
    }

    [Fact]
    public async Task PairingPromptIsRetriedWhenDecisionCannotReachServer()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.ReturnPendingPairing();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "sync-profiles.json"));
        var receivedRequests = new ConcurrentQueue<string>();
        client.PairingRequestReceived += request => receivedRequests.Enqueue(request.Id);

        await client.ActivateDeviceAsync(server.BaseUri);
        await WaitUntilAsync(() => receivedRequests.Count == 1, TimeSpan.FromSeconds(10));
        server.FailNextPairingDecision();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DecidePairingRequestAsync("pairing-request-1", accept: true));
        await WaitUntilAsync(() => receivedRequests.Count == 2, TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "pairing-request-1", "pairing-request-1" }, receivedRequests.ToArray());
    }

    [Fact]
    public async Task PairingPromptIsShownAgainAfterTheUserDismissesIt()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        server.ReturnPendingPairing();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        await using var client = new ClipBridgeSyncClient(repository, Path.Combine(temporary.Path, "sync-profiles.json"));
        var receivedRequests = new ConcurrentQueue<string>();
        client.PairingRequestReceived += request => receivedRequests.Enqueue(request.Id);

        await client.ActivateDeviceAsync(server.BaseUri);
        await WaitUntilAsync(() => receivedRequests.Count == 1, TimeSpan.FromSeconds(10));
        client.DismissPairingPrompt("pairing-request-1");
        await WaitUntilAsync(() => receivedRequests.Count == 2, TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "pairing-request-1", "pairing-request-1" }, receivedRequests.ToArray());
    }

    [Fact]
    public async Task ConnectsPullsPublishesAndResumesFromSavedCursorWithoutSavingSecrets()
    {
        using var temporary = new TemporaryDirectory();
        await using var server = new LocalSyncTestServer();
        var repository = new SqliteClipboardRepository(Path.Combine(temporary.Path, "clipbridge.db"));
        var profilePath = Path.Combine(temporary.Path, "sync-profiles.json");

        await using (var client = new ClipBridgeSyncClient(repository, profilePath))
        {
            try
            {
                await client.ConnectAsync(server.BaseUri, "test-user", "never-save-this-password");
            }
            catch (HttpRequestException) when (server.Failures.TryPeek(out var failure))
            {
                throw new InvalidOperationException("The local sync test server failed to handle a request.", failure);
            }

            Assert.Empty(repository.GetRecentTextItems(10));

            var local = repository.AddText("from-cloud", "CLOUD-DEV")!;
            await client.PublishTextAsync(local);
            Assert.True(repository.FindById(local.Id)!.Synced);
            Assert.Equal(local.Id, server.UploadedId);
            Assert.Equal("from-cloud", server.UploadedText);
        }

        await using (var reconnectingClient = new ClipBridgeSyncClient(repository, profilePath))
        {
            await reconnectingClient.ConnectAsync(server.BaseUri, "test-user", "never-save-this-password");
        }

        Assert.Equal(1, server.DeviceRegistrationCount);
        Assert.All(server.HistoryRequests, request => Assert.Contains("after=", request, StringComparison.Ordinal));
        var savedProfiles = await File.ReadAllTextAsync(profilePath);
        Assert.DoesNotContain("never-save-this-password", savedProfiles, StringComparison.Ordinal);
        Assert.DoesNotContain("test-access-token", savedProfiles, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected sync state was not reached before the timeout.");
            }

            await Task.Delay(50);
        }
    }

    private sealed class LocalSyncTestServer : IAsyncDisposable
    {
        private const string Cursor = "opaque-cursor+";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ConcurrentQueue<string> _newHistoryItems = new();
        private readonly System.Threading.Channels.Channel<byte> _notificationSignals =
            System.Threading.Channels.Channel.CreateBounded<byte>(new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite,
                SingleReader = false,
                SingleWriter = false,
            });
        private readonly Task _acceptTask;
        private int _deviceRegistrationCount;
        private int _anonymousDeviceRegistrationCount;
        private int _pairingRequestCount;
        private int _loginCount;
        private int _expireNextAuthorizedRequest;
        private int _returnPendingPairing;
        private int _returnAlreadyPaired;
        private int _failNextPairingDecision;
        private int _nextHistoryResponseDelayMs;
        private int _disableNotifications;
        private int _failNextHistoryRequest;
        private int _healthRequests;
        private int _imageUploadAttempts;
        private string _activeAccessToken = string.Empty;
        private string? _uploadedId;
        private string? _uploadedText;
        private string _activeDeviceCredential = string.Empty;
        private string? _registeredDeviceCode;
        private string? _pairingRequesterCode;
        private string? _pairingTargetCode;

        public LocalSyncTestServer()
        {
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            _acceptTask = Task.Run(AcceptLoopAsync);
        }

        public Uri BaseUri { get; }
        public int DeviceRegistrationCount => Volatile.Read(ref _deviceRegistrationCount);
        public int AnonymousDeviceRegistrationCount => Volatile.Read(ref _anonymousDeviceRegistrationCount);
        public int PairingRequestCount => Volatile.Read(ref _pairingRequestCount);
        public int LoginCount => Volatile.Read(ref _loginCount);
        public int HealthRequests => Volatile.Read(ref _healthRequests);
        public int ImageUploadAttempts => Volatile.Read(ref _imageUploadAttempts);
        public string? UploadedId => Volatile.Read(ref _uploadedId);
        public string? UploadedText => Volatile.Read(ref _uploadedText);
        public string? RegisteredDeviceCode => Volatile.Read(ref _registeredDeviceCode);
        public string? PairingRequesterCode => Volatile.Read(ref _pairingRequesterCode);
        public string? PairingTargetCode => Volatile.Read(ref _pairingTargetCode);
        public ConcurrentQueue<string> HistoryRequests { get; } = new();
        public ConcurrentQueue<string> ImageRequests { get; } = new();
        public ConcurrentQueue<Exception> Failures { get; } = new();
        public TaskCompletionSource<bool> NotificationConnection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ExpireNextAuthorizedRequest() => Interlocked.Exchange(ref _expireNextAuthorizedRequest, 1);
        public void ReturnPendingPairing() => Interlocked.Exchange(ref _returnPendingPairing, 1);
        public void ReturnAlreadyPaired() => Interlocked.Exchange(ref _returnAlreadyPaired, 1);
        public void FailNextPairingDecision() => Interlocked.Exchange(ref _failNextPairingDecision, 1);
        public void DisableNotifications() => Interlocked.Exchange(ref _disableNotifications, 1);
        public void FailNextHistoryRequest() => Interlocked.Exchange(ref _failNextHistoryRequest, 1);
        public void DelayNextHistoryResponse(TimeSpan delay) => Interlocked.Exchange(ref _nextHistoryResponseDelayMs, checked((int)delay.TotalMilliseconds));
        public void AddRemoteText(string id, long originDeviceId, string originDeviceName, string text, string contentHash)
        {
            _newHistoryItems.Enqueue(JsonSerializer.Serialize(new
            {
                id,
                origin_device_id = originDeviceId,
                origin_device_name = originDeviceName,
                type = "TEXT",
                text,
                content_hash = contentHash,
                created_at = DateTimeOffset.UtcNow,
            }));
            _notificationSignals.Writer.TryWrite(1);
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
            }

            _cancellation.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClientAsync(client);
                    }
                    catch (Exception exception)
                    {
                        Failures.Enqueue(exception);
                        client.Dispose();
                    }
                });
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var request = await ReadRequestAsync(stream, _cancellation.Token);
                if (request is null)
                {
                    return;
                }

                var (requestLine, headers, body) = request.Value;
                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var method = parts[0];
                var target = parts[1];
                var path = target.Split('?', 2)[0];

                if (path == "/api/v1/events" && method == "GET")
                {
                    if (Volatile.Read(ref _disableNotifications) == 1)
                    {
                        await WriteResponseAsync(stream, 404, "{}", _cancellation.Token);
                        return;
                    }

                    var authorized = headers.TryGetValue("Authorization", out var eventAuthorization)
                        && (eventAuthorization == $"Bearer {Volatile.Read(ref _activeDeviceCredential)}"
                            || eventAuthorization == $"Bearer {Volatile.Read(ref _activeAccessToken)}");
                    if (!authorized)
                    {
                        await WriteResponseAsync(stream, 401, "{\"error\":\"unauthorized\"}", _cancellation.Token);
                        return;
                    }

                    await WriteSseNotificationsAsync(stream, _cancellation.Token);
                    return;
                }

                var statusCode = 200;
                string responseBody;

                if (path == "/api/v1/images" && method == "POST")
                    Interlocked.Increment(ref _imageUploadAttempts);

                if (path == "/health" && method == "GET")
                {
                    Interlocked.Increment(ref _healthRequests);
                    responseBody = """{"status":"ok"}""";
                }
                else if (path == "/api/v1/clips" && method == "GET" && Interlocked.Exchange(ref _failNextHistoryRequest, 0) == 1)
                {
                    statusCode = 503;
                    responseBody = """{"error":"service_unavailable"}""";
                }
                else if (path == "/api/v1/auth/login" && method == "POST")
                {
                    var loginNumber = Interlocked.Increment(ref _loginCount);
                    var accessToken = $"test-access-token-{loginNumber}";
                    Volatile.Write(ref _activeAccessToken, accessToken);
                    responseBody = JsonSerializer.Serialize(new
                    {
                        access_token = accessToken,
                        token_type = "Bearer",
                        expires_in = 900,
                        user = new { id = 1, username = "test-user", display_name = "Test User" },
                    });
                }
                else if (path == "/api/v1/devices" && method == "POST"
                    && !headers.ContainsKey("Authorization"))
                {
                    using var registration = JsonDocument.Parse(body);
                    var code = registration.RootElement.GetProperty("device_code").GetString()!;
                    Volatile.Write(ref _registeredDeviceCode, code);
                    Interlocked.Increment(ref _anonymousDeviceRegistrationCount);
                    Volatile.Write(ref _activeDeviceCredential, "test-device-credential-secret");
                    statusCode = 201;
                    responseBody = """{"device":{"id":77,"name":"ClipBridge","machine_name":"TEST-PC","platform":"windows","client_version":"1.0.0","status":"ACTIVE","created_at":"2026-10-03T00:00:00Z"},"credential":"test-device-credential-secret"}""";
                }
                else if (headers.TryGetValue("Authorization", out var deviceAuthorization)
                    && deviceAuthorization == $"Bearer {Volatile.Read(ref _activeDeviceCredential)}")
                {
                    if (path == "/api/v1/pairing-requests" && method == "POST")
                    {
                        using var pairing = JsonDocument.Parse(body);
                        Volatile.Write(ref _pairingRequesterCode, pairing.RootElement.GetProperty("requester_code").GetString());
                        Volatile.Write(ref _pairingTargetCode, pairing.RootElement.GetProperty("target_code").GetString());
                        Interlocked.Increment(ref _pairingRequestCount);
                        if (Interlocked.Exchange(ref _returnAlreadyPaired, 0) == 1)
                        {
                            statusCode = 409;
                            responseBody = """{"error":"already_paired"}""";
                        }
                        else
                        {
                            statusCode = 202;
                            responseBody = """{"id":"pairing-request-1","requester_name":"TEST-PC","requester_code":"TEST12","source_address":"127.0.0.1","expires_at":"2026-10-03T00:05:00Z","created_at":"2026-10-03T00:00:00Z"}""";
                        }
                    }
                    else if (path == "/api/v1/pairing-requests/pending" && method == "GET")
                    {
                        responseBody = Volatile.Read(ref _returnPendingPairing) == 1
                            ? """{"items":[{"id":"pairing-request-1","requester_name":"VM-TEST","requester_code":"PEER12","source_address":"127.0.0.1","expires_at":"2026-10-03T00:05:00Z","created_at":"2026-10-03T00:00:00Z"}]}"""
                            : """{"items":[]}""";
                    }
                    else if (path == "/api/v1/pairing-requests/pairing-request-1/decision" && method == "POST")
                    {
                        statusCode = Interlocked.Exchange(ref _failNextPairingDecision, 0) == 1 ? 500 : 204;
                        responseBody = "{}";
                    }
                    else if (path == "/api/v1/clips" && method == "GET")
                    {
                        HistoryRequests.Enqueue(target);
                        if (_newHistoryItems.TryDequeue(out var nextHistoryItem))
                        {
                            using var itemDocument = JsonDocument.Parse(nextHistoryItem);
                            responseBody = JsonSerializer.Serialize(new
                            {
                                items = new[] { itemDocument.RootElement.Clone() },
                                next_cursor = "poll-cursor",
                                has_more = false,
                            });
                        }
                        else
                        {
                            responseBody = """{"items":[],"has_more":false}""";
                        }
                    }
                    else if (path == "/api/v1/images" && method == "GET")
                    {
                        ImageRequests.Enqueue(target);
                        responseBody = """{"items":[],"has_more":false}""";
                    }
                    else
                    {
                        statusCode = 404;
                        responseBody = "{}";
                    }
                }
                else if (headers.TryGetValue("Authorization", out var authorization)
                    && authorization == $"Bearer {Volatile.Read(ref _activeAccessToken)}")
                {
                    if (Interlocked.Exchange(ref _expireNextAuthorizedRequest, 0) == 1)
                    {
                        statusCode = 401;
                        responseBody = """{"error":"unauthorized"}""";
                    }
                    else if (path == "/api/v1/devices" && method == "POST")
                    {
                        Interlocked.Increment(ref _deviceRegistrationCount);
                        statusCode = 201;
                        responseBody = """{"id":77,"name":"CLOUD-DEV"}""";
                    }
                    else if (path == "/api/v1/clips" && method == "POST")
                    {
                        using var upload = JsonDocument.Parse(body);
                        Volatile.Write(ref _uploadedId, upload.RootElement.GetProperty("id").GetString());
                        Volatile.Write(ref _uploadedText, upload.RootElement.GetProperty("text").GetString());
                        statusCode = 201;
                        responseBody = "{}";
                    }
                    else if (path == "/api/v1/images" && method == "POST")
                    {
                        statusCode = 201;
                        responseBody = "{}";
                    }
                    else if (path == "/api/v1/clips" && method == "GET")
                    {
                        HistoryRequests.Enqueue(target);
                        if (target.Contains("after=", StringComparison.Ordinal)
                            && _newHistoryItems.TryDequeue(out var nextHistoryItem))
                        {
                            using var itemDocument = JsonDocument.Parse(nextHistoryItem);
                            responseBody = JsonSerializer.Serialize(new
                            {
                                items = new[] { itemDocument.RootElement.Clone() },
                                next_cursor = "poll-cursor",
                                has_more = false,
                            });
                        }
                        else
                        {
                            responseBody = target.Contains("after=", StringComparison.Ordinal)
                                ? """{"items":[],"has_more":false}"""
                                : """{"items":[{"id":"remote-clip-1","origin_device_id":88,"origin_device_name":"HOST-PC","type":"TEXT","text":"from-host","content_hash":"test-hash","created_at":"2026-09-30T01:02:03Z"}],"next_cursor":"opaque-cursor+","has_more":false}""";
                        }
                    }
                    else if (path == "/api/v1/images" && method == "GET")
                    {
                        ImageRequests.Enqueue(target);
                        responseBody = """{"items":[],"has_more":false}""";
                    }
                    else
                    {
                        statusCode = 404;
                        responseBody = "{}";
                    }
                }
                else
                {
                    statusCode = 401;
                    responseBody = """{"error":"unauthorized"}""";
                }

                var historyDelayMs = path == "/api/v1/clips" && method == "GET"
                    ? Interlocked.Exchange(ref _nextHistoryResponseDelayMs, 0)
                    : 0;
                if (historyDelayMs > 0)
                {
                    await Task.Delay(historyDelayMs, _cancellation.Token);
                }

                await WriteResponseAsync(stream, statusCode, responseBody, _cancellation.Token);
            }
        }

        private static async Task<(string RequestLine, Dictionary<string, string> Headers, string Body)?> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            using var request = new MemoryStream();
            var byteBuffer = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(byteBuffer, cancellationToken);
                if (read == 0)
                {
                    return null;
                }

                request.WriteByte(byteBuffer[0]);
                var length = request.Length;
                if (length >= 4)
                {
                    var buffer = request.GetBuffer();
                    if (buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                    {
                        break;
                    }
                }
            }

            var headerText = Encoding.ASCII.GetString(request.GetBuffer(), 0, checked((int)request.Length));
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            byte[] bodyBytes;
            if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
                && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                bodyBytes = await ReadChunkedBodyAsync(stream, cancellationToken);
            }
            else
            {
                var bodyLength = headers.TryGetValue("Content-Length", out var rawLength) ? int.Parse(rawLength) : 0;
                bodyBytes = new byte[bodyLength];
                var offset = 0;
                while (offset < bodyLength)
                {
                    offset += await stream.ReadAsync(bodyBytes.AsMemory(offset), cancellationToken);
                }
            }

            return (lines[0], headers, Encoding.UTF8.GetString(bodyBytes));
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadAsciiLineAsync(stream, cancellationToken);
                var sizeToken = sizeLine.Split(';', 2)[0];
                var chunkSize = Convert.ToInt32(sizeToken, 16);
                if (chunkSize == 0)
                {
                    _ = await ReadAsciiLineAsync(stream, cancellationToken);
                    return body.ToArray();
                }

                var chunk = new byte[chunkSize];
                var offset = 0;
                while (offset < chunk.Length)
                {
                    offset += await stream.ReadAsync(chunk.AsMemory(offset), cancellationToken);
                }

                await body.WriteAsync(chunk, cancellationToken);
                _ = await ReadAsciiLineAsync(stream, cancellationToken);
            }
        }

        private static async Task<string> ReadAsciiLineAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();
            var buffer = new byte[1];
            while (await stream.ReadAsync(buffer, cancellationToken) > 0)
            {
                if (buffer[0] == '\n')
                {
                    var bytes = line.ToArray();
                    return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, bytes.Length - 1));
                }

                line.WriteByte(buffer[0]);
            }

            throw new EndOfStreamException("The local test server received an incomplete chunked body.");
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string body, CancellationToken cancellationToken)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var reason = statusCode switch { 200 => "OK", 201 => "Created", 401 => "Unauthorized", _ => "Not Found" };
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(bodyBytes, cancellationToken);
        }

        private async Task WriteSseNotificationsAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n: connected\n\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            NotificationConnection.TrySetResult(true);

            while (await _notificationSignals.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_notificationSignals.Reader.TryRead(out _))
                {
                    var message = Encoding.UTF8.GetBytes("id: 1\nevent: clipboard\ndata: changed\n\n");
                    await stream.WriteAsync(message, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipBridge.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
    }
}
