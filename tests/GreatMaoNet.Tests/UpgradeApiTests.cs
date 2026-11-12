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

public sealed class UpgradeApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task RealBeeNotifiesBeforeShutdownAndApisShowUpgradeUntilReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        using var directory = new TemporaryDirectory();
        var options = new BeeOptions
        {
            Id = "socket-20", Name = "Upgrade Bee", DatabasePath = directory.File("bee.db"),
            HiveUrl = new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri.ToString(),
            Token = "socket-20-test-credential".PadRight(40, 'x'), AllowInsecureTransport = true
        };
        using var beeStore = new BeeStore(options);
        using var sampling = new SamplingService(beeStore, new EmptyCounterSource(), NullLogger<SamplingService>.Instance);
        var state = new BeeConnectionState();
        using var bee = new HiveConnectionService(options, beeStore, sampling, state, NullLogger<HiveConnectionService>.Instance);
        await bee.StartAsync(token);
        try
        {
            await WaitAsync(() => state.Snapshot().State == "Connected", token);
            await bee.PrepareAsync(token);
            await AssertUpgradingAsync(options.Id, connected: true, token);
            await bee.CancelAsync(token);
            Assert.Equal("online", (await DetailAsync(options.Id, token)).Status);
            var from = DateTimeOffset.UtcNow;
            await bee.PrepareAsync(token);
            await bee.StopAsync(token);
            var sessions = fixture.Application.Services.GetRequiredService<BeeSessions>();
            await WaitAsync(() => sessions.Get(options.Id) is null, token);
            await AssertUpgradingAsync(options.Id, connected: false, token);
            await Task.Delay(30, token);

            using (var invalid = new ClientWebSocket())
            {
                invalid.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
                await invalid.ConnectAsync(new Uri(options.HiveUrl), token);
                using var protocol = new WebSocketProtocol(invalid);
                await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(TestData.Node(options.Id), 0, 0) }, token);
                Assert.Equal("error", (await protocol.ReceiveAsync(token))!.Type);
                Assert.Null(await protocol.ReceiveAsync(token));
                Assert.Equal(WebSocketCloseStatus.PolicyViolation, invalid.CloseStatus);
                await invalid.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
            }
            await WaitAsync(() => sessions.Get(options.Id) is null, token);
            await AssertUpgradingAsync(options.Id, connected: false, token);

            // Start the replacement with the same durable Bee database.
            var restoredState = new BeeConnectionState();
            using var replacement = new HiveConnectionService(options, beeStore, sampling, restoredState, NullLogger<HiveConnectionService>.Instance);
            await replacement.StartAsync(token);
            try
            {
                await WaitAsync(() => restoredState.Snapshot().State == "Connected", token);
                await WaitAsync(() => !fixture.Application.Services.GetRequiredService<HiveStore>().UpgradingNodes(DateTimeOffset.UtcNow).Contains(options.Id), token);
                Assert.Equal("online", (await DetailAsync(options.Id, token)).Status);
                var to = DateTimeOffset.UtcNow;
                await fixture.Application.Services.GetRequiredService<DatabaseWriter>().RunAsync(store => { store.Observe(to); return true; }, token);
                string window = $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
                var history = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync($"/api/v1/bees/{options.Id}/online/history{window}", token), ProtocolJson.Default.OnlineHistoryResponse)!;
                Assert.True(history.UpgradingSeconds > 0);
                Assert.Equal(0, history.OfflineSeconds);
                Assert.Equal((history.To - history.From).TotalSeconds,
                    history.OnlineSeconds + history.OfflineSeconds + history.UnknownSeconds + history.UpgradingSeconds, 6);
            }
            finally { await replacement.StopAsync(token); }
        }
        finally { await bee.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task HiveUpdateHookPersistsProtectionAndCancellationRestoresOnlineStatus()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + "socket-21-test-credential".PadRight(40, 'x'));
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri, token);
        using var protocol = new WebSocketProtocol(socket);
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(TestData.Node("socket-21"), 0, 0) }, token);
        var welcome = await protocol.ReceiveAsync(token);
        Assert.Contains("upgrade-notice-v1", welcome!.Capabilities!);
        await protocol.SendAsync(new WireMessage { Type = "ping" }, token);
        Assert.Equal("pong", (await protocol.ReceiveAsync(token))!.Type);
        var preparation = fixture.Application.Services.GetRequiredService<IUpgradePreparation>();
        try
        {
            await preparation.PrepareAsync(token);
            await AssertUpgradingAsync("socket-21", connected: true, token);
            var store = fixture.Application.Services.GetRequiredService<HiveStore>();
            Assert.DoesNotContain("socket-99", store.UpgradingNodes(DateTimeOffset.UtcNow));
        }
        finally { await preparation.CancelAsync(token); }
        Assert.Equal("online", (await DetailAsync("socket-21", token)).Status);
    }

    private async Task AssertUpgradingAsync(string id, bool connected, CancellationToken token)
    {
        var detail = await DetailAsync(id, token);
        Assert.Equal("upgrading", detail.Status);
        Assert.True(detail.Upgrading);
        Assert.Equal(connected, detail.Online);
        var list = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync("/api/v1/bees", token), ProtocolJson.Default.BeeSummaryArray)!;
        Assert.Equal("upgrading", list.Single(x => x.Id == id).Status);
        var quotas = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync("/api/v1/bees/traffic/quotas", token), ProtocolJson.Default.TrafficQuotasResponse)!;
        Assert.Equal("upgrading", quotas.Bees.Single(x => x.Id == id).Status);
        var total = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync($"/api/v1/bees/{id}/traffic/total", token), ProtocolJson.Default.TotalResponse)!;
        Assert.Equal("upgrading", total.Status);
        using var current = await fixture.Client.GetAsync($"/api/v1/bees/{id}/traffic/current", token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, current.StatusCode);
        Assert.Contains("Bee is upgrading.", await current.Content.ReadAsStringAsync(token));
        Assert.Equal("upgrading", fixture.Application.Services.GetRequiredService<BeeSessions>().Snapshot([id]).Single().Status);
        Assert.Contains(fixture.Application.Services.GetRequiredService<IConsoleStatusProvider>().GetStatus(), line => line.Contains($"{id}: upgrading", StringComparison.Ordinal));
    }

    private async Task<BeeDetail> DetailAsync(string id, CancellationToken token) =>
        JsonSerializer.Deserialize(await fixture.Client.GetStringAsync($"/api/v1/bees/{id}", token), ProtocolJson.Default.BeeDetail)!;

    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(10, token);
    }

    private sealed class EmptyCounterSource : ICounterSource
    {
        public string BootId => "test";
        public CounterReading[] Read(double monotonicSeconds) => [];
    }
}
