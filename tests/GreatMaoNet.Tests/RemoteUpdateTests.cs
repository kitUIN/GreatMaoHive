using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using GreatMaoNet.Contracts;
using GreatMaoNet.Updates;
using GreatMaoNetBee;
using GreatMaoNetHive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreatMaoNet.Tests;

public sealed class RemoteUpdateTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData(null)]
    [InlineData(ApiFixture.ReadKey)]
    [InlineData(ApiFixture.BeeKey)]
    [InlineData("wrong-key")]
    public async Task UpdateRequiresSeparateAdminCredential(string? key)
    {
        using var client = Client(key);
        using var response = await client.PostAsync("/api/v1/bees/bee-01/update", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task UnknownAndOfflineNodesAreNotQueued()
    {
        using var client = Client(ApiFixture.AdminKey);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/bees/missing/update", null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/v1/bees/socket-99/update", null)).StatusCode);
    }

    [Fact]
    public async Task LegacyBeeRemainsConnectedAfterUnsupportedRequest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var peer = await ConnectAsync("socket-30", false, timeout.Token);
        using var client = Client(ApiFixture.AdminKey);
        using var response = await client.PostAsync("/api/v1/bees/socket-30/update", null, timeout.Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("does not support", await response.Content.ReadAsStringAsync(timeout.Token));
        await peer.Protocol.SendAsync(new WireMessage { Type = "ping" }, timeout.Token);
        Assert.Equal("pong", (await peer.Protocol.ReceiveAsync(timeout.Token))!.Type);
    }

    [Fact]
    public async Task AcknowledgementIsCorrelatedAndConcurrentRequestsAreRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var peer = await ConnectAsync("socket-31", true, token);
        using var client = Client(ApiFixture.AdminKey);
        var pending = client.PostAsync("/api/v1/bees/socket-31/update", null, token);
        var request = await peer.Protocol.ReceiveAsync(token);
        Assert.Equal("update-request", request!.Type);
        Assert.True(Guid.TryParseExact(request.RequestId, "N", out _));
        await peer.Protocol.SendAsync(new WireMessage { Type = "update-response", RequestId = Guid.NewGuid().ToString("N"), UpdateStatus = "accepted" }, token);
        await peer.Protocol.SendAsync(new WireMessage { Type = "ping" }, token);
        Assert.Equal("pong", (await peer.Protocol.ReceiveAsync(token))!.Type);
        Assert.False(pending.IsCompleted);
        using var duplicate = await client.PostAsync("/api/v1/bees/socket-31/update", null, token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await peer.Protocol.SendAsync(new WireMessage { Type = "update-response", RequestId = request.RequestId, UpdateStatus = "accepted" }, token);
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(token), ProtocolJson.Default.BeeUpdateResponse)!;
        Assert.Equal(new BeeUpdateResponse("socket-31", request.RequestId!, "accepted"), body);
    }

    [Theory]
    [InlineData("busy", HttpStatusCode.Conflict)]
    [InlineData("disabled", HttpStatusCode.Conflict)]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task BeeRejectionIsReturnedToCaller(string status, HttpStatusCode expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string id = status == "busy" ? "socket-32" : status == "disabled" ? "socket-33" : "socket-34";
        using var peer = await ConnectAsync(id, true, timeout.Token);
        using var client = Client(ApiFixture.AdminKey);
        var pending = client.PostAsync($"/api/v1/bees/{id}/update", null, timeout.Token);
        var request = await peer.Protocol.ReceiveAsync(timeout.Token);
        await peer.Protocol.SendAsync(new WireMessage { Type = "update-response", RequestId = request!.RequestId, UpdateStatus = status }, timeout.Token);
        using var response = await pending;
        Assert.Equal(expected, response.StatusCode);
        Assert.Contains(status, await response.Content.ReadAsStringAsync(timeout.Token));
    }

    [Fact]
    public async Task TimeoutDoesNotClaimSuccessAndLateReplyDoesNotBreakSession()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var peer = await ConnectAsync("socket-35", true, timeout.Token);
        using var client = Client(ApiFixture.AdminKey);
        var pending = client.PostAsync("/api/v1/bees/socket-35/update", null, timeout.Token);
        var request = await peer.Protocol.ReceiveAsync(timeout.Token);
        using var response = await pending;
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("unknown", await response.Content.ReadAsStringAsync(timeout.Token));
        await peer.Protocol.SendAsync(new WireMessage { Type = "update-response", RequestId = request!.RequestId, UpdateStatus = "accepted" }, timeout.Token);
        await peer.Protocol.SendAsync(new WireMessage { Type = "ping" }, timeout.Token);
        Assert.Equal("pong", (await peer.Protocol.ReceiveAsync(timeout.Token))!.Type);
    }

    [Fact]
    public async Task DisconnectWhileWaitingDoesNotClaimSuccess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var peer = await ConnectAsync("socket-36", true, timeout.Token);
        using var client = Client(ApiFixture.AdminKey);
        var pending = client.PostAsync("/api/v1/bees/socket-36/update", null, timeout.Token);
        Assert.Equal("update-request", (await peer.Protocol.ReceiveAsync(timeout.Token))!.Type);
        peer.Socket.Abort();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await pending).StatusCode);
    }

    [Fact]
    public async Task RealBeeQueuesUpdateWithAutomaticChecksDisabledAndStillProcessesUpgradeNotices()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var directory = new TemporaryDirectory();
        var options = new BeeOptions
        {
            Id = "socket-37", Name = "Remote Update Bee", DatabasePath = directory.File("bee.db"),
            HiveUrl = new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri.ToString(),
            Token = "socket-37-test-credential".PadRight(40, 'x'), AllowInsecureTransport = true
        };
        using var queue = new UpdateRequestQueue(new UpdateOptions { Enabled = false });
        queue.SetAvailable(true);
        using var store = new BeeStore(options);
        using var sampling = new SamplingService(store, new EmptyCounterSource(), NullLogger<SamplingService>.Instance);
        var state = new BeeConnectionState();
        using var bee = new HiveConnectionService(options, store, sampling, state, NullLogger<HiveConnectionService>.Instance, queue);
        await bee.StartAsync(token);
        try
        {
            var sessions = fixture.Application.Services.GetRequiredService<BeeSessions>();
            while (sessions.Get(options.Id)?.Online != true) await Task.Delay(10, token);
            using var client = Client(ApiFixture.AdminKey);
            using var response = await client.PostAsync($"/api/v1/bees/{options.Id}/update", null, token);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.True(await queue.WaitAsync(TimeSpan.Zero, token));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/bees/{options.Id}/update", null, token)).StatusCode);
            await bee.PrepareAsync(token);
            Assert.Contains(options.Id, fixture.Application.Services.GetRequiredService<HiveStore>().UpgradingNodes(DateTimeOffset.UtcNow));
            await bee.CancelAsync(token);
            Assert.Equal("Connected", state.Snapshot().State);
        }
        finally { await bee.StopAsync(CancellationToken.None); }
    }

    private HttpClient Client(string? key)
    {
        var client = new HttpClient { BaseAddress = fixture.Address };
        if (key is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return client;
    }

    private async Task<Peer> ConnectAsync(string id, bool remoteUpdate, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + $"{id}-test-credential".PadRight(40, 'x'));
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri, token);
        var protocol = new WebSocketProtocol(socket);
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(TestData.Node(id), 0, 0),
            Capabilities = remoteUpdate ? ["remote-update-v1"] : null }, token);
        Assert.Equal("welcome", (await protocol.ReceiveAsync(token))!.Type);
        await protocol.SendAsync(new WireMessage { Type = "ping" }, token);
        Assert.Equal("pong", (await protocol.ReceiveAsync(token))!.Type);
        return new(socket, protocol);
    }

    private sealed record Peer(ClientWebSocket Socket, WebSocketProtocol Protocol) : IDisposable
    {
        public void Dispose() { Socket.Abort(); Protocol.Dispose(); Socket.Dispose(); }
    }

    private sealed class EmptyCounterSource : ICounterSource
    {
        public string BootId => "test";
        public CounterReading[] Read(double monotonicSeconds) => [];
    }
}

public sealed class UpdateRequestQueueTests
{
    [Fact]
    public async Task OnlyOneConcurrentRemoteOrAutomaticUpdateCanRunAndWorkerCanRetry()
    {
        using var queue = new UpdateRequestQueue(new UpdateOptions { Enabled = false });
        Assert.Equal("unavailable", queue.RequestUpdate());
        queue.SetAvailable(true);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(queue.RequestUpdate)));
        Assert.Single(results, x => x == "accepted");
        Assert.Equal(39, results.Count(x => x == "busy"));
        Assert.False(queue.TryBeginAutomatic());
        Assert.True(await queue.WaitAsync(TimeSpan.Zero, default));
        queue.Complete();
        Assert.True(queue.TryBeginAutomatic());
        Assert.Equal("busy", queue.RequestUpdate());
        queue.Complete();
        var waiting = queue.WaitAsync(Timeout.InfiniteTimeSpan, default);
        Assert.Equal("accepted", queue.RequestUpdate());
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        queue.Complete();
        queue.SetAvailable(false);
        Assert.Equal("unavailable", queue.RequestUpdate());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void RemoteUpdateHonorsLocalPolicy(bool allowRemote, bool download, bool apply)
    {
        using var queue = new UpdateRequestQueue(new UpdateOptions { AllowRemoteUpdates = allowRemote, AutoDownload = download, AutoApply = apply });
        queue.SetAvailable(true);
        Assert.Equal("disabled", queue.RequestUpdate());
        Assert.True(queue.TryBeginAutomatic());
    }

    [Theory]
    [InlineData("short")]
    [InlineData(ApiFixture.ReadKey)]
    [InlineData(ApiFixture.BeeKey)]
    public void AdminKeyCannotBeWeakOrReuseReadOrNodeCredential(string key)
    {
        var options = new HiveOptions { ReadApiKey = ApiFixture.ReadKey, AdminApiKey = key,
            Bees = [new BeeCredential { Id = "bee", Token = ApiFixture.BeeKey }] };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void EmptyAdminKeyDisablesAuthenticationAndPreservesExistingConfig()
    {
        var options = new HiveOptions { ReadApiKey = ApiFixture.ReadKey,
            Bees = [new BeeCredential { Id = "bee", Token = ApiFixture.BeeKey }] };
        options.Validate();
        Assert.False(HiveOptions.Matches("Bearer ", options.AdminApiKey));
        Assert.False(HiveOptions.Matches("Bearer " + ApiFixture.ReadKey, options.AdminApiKey));
    }
}
