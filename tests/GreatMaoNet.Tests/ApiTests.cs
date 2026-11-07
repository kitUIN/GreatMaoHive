using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using GreatMaoNet.Contracts;
using GreatMaoNetHive;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GreatMaoNet.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    public const string ReadKey = "read-key-for-integration-tests-only-000000";
    public const string BeeKey = "bee-key-for-integration-tests-only-0000000";
    public TemporaryDirectory Directory { get; } = new();
    public WebApplication Application { get; private set; } = null!;
    public Uri Address { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        var arguments = new List<string> { "--Hive:Bees:0:Quota:LimitBytes", "100", "--Updates:Enabled", "false", "--urls", "http://127.0.0.1:0", "--Hive:DatabasePath", Directory.File("hive.db"),
            "--Hive:ReadApiKey", ReadKey, "--Hive:Bees:0:Id", "bee-01", "--Hive:Bees:0:Token", BeeKey,
            "--Hive:AllowInsecureTransport", "true", "--Logging:LogLevel:Default", "None" };
        for (int i = 0; i < 100; i++)
            arguments.AddRange([$"--Hive:Bees:{i + 1}:Id", $"socket-{i}", $"--Hive:Bees:{i + 1}:Token", $"socket-{i}-test-credential".PadRight(40, 'x')]);
        Application = HiveApplication.Build(arguments.ToArray());
        await Application.StartAsync();
        Address = new(Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        Client = new HttpClient { BaseAddress = Address };
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ReadKey);
    }
    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Application.StopAsync();
        await Application.DisposeAsync();
        Directory.Dispose();
    }
}

public sealed class ApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task HealthAuthenticationNotFoundAndProblemDetails()
    {
        using var anonymous = new HttpClient { BaseAddress = fixture.Address };
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/healthz")).StatusCode);
        var unauthorized = await anonymous.GetAsync("/api/v1/bees");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("application/problem+json", unauthorized.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/v1/bees/missing/traffic/current")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", ApiFixture.BeeKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/bees")).StatusCode);
    }

    [Fact]
    public async Task WebSocketUploadDuplicateReplayRealTimeTimeoutAndOfflineQueries()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = cancellation.Token;
        var node = TestData.Node() with { Region = "中国香港", Ip = "2001:db8::10" };
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + ApiFixture.BeeKey);
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri, token);
        using var protocol = new WebSocketProtocol(socket);
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(node, 0, 0) }, token);
        Assert.Equal("welcome", (await protocol.ReceiveAsync(token))!.Type);
        var batch = new WireMessage { Type = "batch", Batch = new([TestData.Record(1)], 0) };
        await protocol.SendAsync(batch, token);
        Assert.Equal(1, (await protocol.ReceiveAsync(token))!.AcknowledgedSequence);
        await protocol.SendAsync(batch, token);
        Assert.Equal(1, (await protocol.ReceiveAsync(token))!.AcknowledgedSequence);
        var totalJson = await fixture.Client.GetStringAsync("/api/v1/bees/bee-01/traffic/total", token);
        Assert.Equal(150, JsonSerializer.Deserialize(totalJson, ProtocolJson.Default.TotalResponse)!.Totals!.TotalBytes);
        await AssertMetadataAsync();

        var currentRequest = fixture.Client.GetAsync("/api/v1/bees/bee-01/traffic/current", token);
        var command = await protocol.ReceiveAsync(token);
        Assert.Equal("current-request", command!.Type);
        await protocol.SendAsync(new WireMessage { Type = "current-response", RequestId = command.RequestId,
            Current = new(DateTimeOffset.UtcNow, 1, 800, 400, true, null) }, token);
        var current = await currentRequest;
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(800, JsonSerializer.Deserialize(await current.Content.ReadAsStringAsync(token), ProtocolJson.Default.CurrentTraffic)!.DownloadBitsPerSecond);

        var timeoutRequest = fixture.Client.GetAsync("/api/v1/bees/bee-01/traffic/current", token);
        Assert.Equal("current-request", (await protocol.ReceiveAsync(token))!.Type);
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await timeoutRequest).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.GetAsync("/api/v1/bees/bee-01/traffic/history?from=bad&to=bad", token)).StatusCode);
        string from = Uri.EscapeDataString(TestData.Now.ToString("O"));
        string to = Uri.EscapeDataString(TestData.Now.AddMinutes(1).ToString("O"));
        var history = await fixture.Client.GetAsync($"/api/v1/bees/bee-01/traffic/history?from={from}&to={to}&step=5s", token);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var points = JsonSerializer.Deserialize(await history.Content.ReadAsStringAsync(token), ProtocolJson.Default.HistoryResponse)!.Points;
        Assert.Equal(100, points[0].ReceivedBytes);
        Assert.Null(points[1].DownloadBitsPerSecond);
        socket.Abort();
        for (int i = 0; i < 100 && fixture.Application.Services.GetRequiredService<BeeSessions>().Get("bee-01") is not null; i++)
            await Task.Delay(20, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await fixture.Client.GetAsync("/api/v1/bees/bee-01/traffic/current", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/v1/bees/bee-01/traffic/total", token)).StatusCode);
        await AssertMetadataAsync();

        async Task AssertMetadataAsync()
        {
            var detail = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync("/api/v1/bees/bee-01", token), ProtocolJson.Default.BeeDetail)!;
            Assert.Equal(node.Region, detail.Node.Region);
            Assert.Equal(node.Ip, detail.Node.Ip);
            var bees = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync("/api/v1/bees", token), ProtocolJson.Default.BeeSummaryArray)!;
            var summary = Assert.Single(bees, bee => bee.Id == node.Id);
            Assert.Equal(node.Region, summary.Region);
            Assert.Equal(node.Ip, summary.Ip);
            var quotas = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync("/api/v1/bees/traffic/quotas", token), ProtocolJson.Default.TrafficQuotasResponse)!;
            var quota = Assert.Single(quotas.Bees, bee => bee.Id == node.Id);
            Assert.Equal(node.Region, quota.Region);
            Assert.Equal(node.Ip, quota.Ip);
            Assert.All(quotas.Bees.Where(bee => bee.Id != node.Id), bee =>
            {
                Assert.Null(bee.Region);
                Assert.Null(bee.Ip);
            });
        }
    }

    [Fact]
    public async Task HundredConcurrentNodesUseBoundedWriterAndReplaySafely()
    {
        var writer = fixture.Application.Services.GetRequiredService<DatabaseWriter>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var jobs = Enumerable.Range(0, 100).Select(async i =>
        {
            var node = TestData.Node($"load-{i}");
            await writer.RunAsync(store => store.Register(new(node, 0, 0), TestData.Now), cancellation.Token);
            for (int batch = 0; batch < 3; batch++)
            {
                var payload = new BatchMessage(Enumerable.Range(batch * 10 + 1, 10).Select(n => TestData.Record(n)).ToArray(), 0);
                await writer.RunAsync(store => store.Ingest(node.Id, payload, TestData.Now.AddMinutes(5)), cancellation.Token);
                await writer.RunAsync(store => store.Ingest(node.Id, payload, TestData.Now.AddMinutes(5)), cancellation.Token);
            }
        });
        await Task.WhenAll(jobs);
        var stored = fixture.Application.Services.GetRequiredService<HiveStore>().List().Where(x => x.Node.Id.StartsWith("load-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(100, stored.Length);
        Assert.All(stored, x => { Assert.Equal(30, x.Sequence); Assert.Equal(4500, x.Totals!.TotalBytes); });
    }

    [Fact]
    public async Task HundredWebSocketConnectionsUploadAndReplayAcrossFourSamplingPeriods()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = cancellation.Token;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + $"socket-{i}-test-credential".PadRight(40, 'x'));
            await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri, token);
            using var protocol = new WebSocketProtocol(socket);
            var node = TestData.Node($"socket-{i}");
            await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(node, 0, 0) }, token);
            Assert.Equal("welcome", (await protocol.ReceiveAsync(token))!.Type);
            for (int n = 1; n <= 4; n++)
            {
                var message = new WireMessage { Type = "batch", Batch = new([TestData.Record(n)], 0) };
                await protocol.SendAsync(message, token);
                Assert.Equal(n, (await protocol.ReceiveAsync(token))!.AcknowledgedSequence);
                await protocol.SendAsync(message, token);
                Assert.Equal(n, (await protocol.ReceiveAsync(token))!.AcknowledgedSequence);
                if (n < 4) await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            socket.Abort();
        }));
        var nodes = fixture.Application.Services.GetRequiredService<HiveStore>().List().Where(x => x.Node.Id.StartsWith("socket-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(100, nodes.Length);
        Assert.All(nodes, x => { Assert.Equal(4, x.Sequence); Assert.Equal(600, x.Totals!.TotalBytes); });
    }
}
