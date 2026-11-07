using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using GreatMaoNet.Contracts;
using GreatMaoNetHive;
using Microsoft.Extensions.DependencyInjection;

namespace GreatMaoNet.Tests;

public sealed class QuotaOnlineApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task AuthenticatedApiIncludesUnseenNodesAndPersistsWebSocketConnectionHistory()
    {
        using var anonymous = new HttpClient { BaseAddress = fixture.Address };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/bees/traffic/quotas")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/bees/bee-01/online/history")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFixture.BeeKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/bees/traffic/quotas")).StatusCode);
        var empty = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync("/api/v1/bees/traffic/quotas"), ProtocolJson.Default.TrafficQuotasResponse)!;
        Assert.Equal(101, empty.Bees.Length);
        Assert.All(empty.Bees, x => { Assert.Null(x.TotalBytes); Assert.False(x.Online); });
        var options = fixture.Application.Services.GetRequiredService<HiveOptions>();
        Assert.Equal(100, options.Bees.Single(x => x.Id == "bee-01").Quota.LimitBytes);

        var from = DateTimeOffset.UtcNow.AddSeconds(-1);
        var node = TestData.Node();
        using var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = tokenSource.Token;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + ApiFixture.BeeKey);
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri, token);
        using var protocol = new WebSocketProtocol(socket);
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(node, 0, 0) }, token);
        Assert.Equal("welcome", (await protocol.ReceiveAsync(token))!.Type);
        var sampled = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 5 * 5);
        await protocol.SendAsync(new WireMessage { Type = "batch", Batch = new([TestData.Record(1, sampled)], 0) }, token);
        Assert.Equal("ack", (await protocol.ReceiveAsync(token))!.Type);
        var quotas = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync("/api/v1/bees/traffic/quotas", token), ProtocolJson.Default.TrafficQuotasResponse)!;
        var bee = quotas.Bees.Single(x => x.Id == "bee-01");
        Assert.Equal(150, bee.TotalBytes);
        Assert.Equal(0, bee.RemainingBytes);
        Assert.True(bee.IsExceeded);
        Assert.True(bee.Online);
        // Exceeding quota must not prevent further uploads.
        await protocol.SendAsync(new WireMessage { Type = "batch", Batch = new([TestData.Record(2, sampled.AddSeconds(5))], 0) }, token);
        Assert.Equal(2, (await protocol.ReceiveAsync(token))!.AcknowledgedSequence);
        await Task.Delay(50, token);
        socket.Abort();
        var writer = fixture.Application.Services.GetRequiredService<DatabaseWriter>();
        for (int i = 0; fixture.Application.Services.GetRequiredService<BeeSessions>().Get("bee-01") is not null && i < 100; i++)
            await Task.Delay(20, token);
        await Task.Delay(50, token);
        var to = DateTimeOffset.UtcNow;
        await writer.RunAsync(store => { store.Observe(to); return true; }, token);
        string window = $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var history = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync("/api/v1/bees/bee-01/online/history" + window, token), ProtocolJson.Default.OnlineHistoryResponse)!;
        Assert.True(history.OnlineSeconds > 0);
        Assert.True(history.OfflineSeconds > 0);
        Assert.Contains(history.Intervals, x => x.Status == "online");
        Assert.Equal((history.To - history.From).TotalSeconds, history.OnlineSeconds + history.OfflineSeconds + history.UnknownSeconds, precision: 6);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/api/v1/bees/socket-0/online/history" + window, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/v1/bees/missing/online/history" + window, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.GetAsync("/api/v1/bees/bee-01/online/history?from=bad&to=bad", token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.GetAsync("/api/v1/bees/bee-01/online/history", token)).StatusCode);
    }
}
