using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GreatMaoNet.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GreatMaoNet.Tests;

public sealed class ProtocolCompressionTests(ITestOutputHelper output)
{
    [Fact]
    public void CompactWireFormatRetainsZeroValuesAndLegacyCompatibilityWithoutChangingApiJson()
    {
        var message = new WireMessage { Type = "ack", AcknowledgedSequence = 0 };
        string compact = JsonSerializer.Serialize(message, WireJson.Default.WireMessage);
        Assert.DoesNotContain(":null", compact);
        Assert.Equal(message, JsonSerializer.Deserialize(compact, ProtocolJson.Default.WireMessage));
        string legacy = JsonSerializer.Serialize(message, ProtocolJson.Default.WireMessage);
        Assert.Contains("\"hello\":null", legacy);
        Assert.Equal(message, JsonSerializer.Deserialize(legacy, WireJson.Default.WireMessage));
        using var api = JsonDocument.Parse(JsonSerializer.Serialize(new CurrentTraffic(TestData.Now, 0, null, null, false, null),
            ProtocolJson.Default.CurrentTraffic));
        Assert.Equal(JsonValueKind.Null, api.RootElement.GetProperty("downloadBitsPerSecond").ValueKind);
        Assert.False(api.RootElement.GetProperty("available").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedFramesRoundTripWithAndWithoutCompression(bool compression)
    {
        WireMessage[] messages = [
            new() { Type = "hello", Hello = new(TestData.Node(), 0, 0) },
            new() { Type = "batch", Batch = new([TestData.Record(1)], 0) },
            new() { Type = "ping" },
            new() { Type = "current-response", RequestId = "sample", Current = new(TestData.Now, 1, 0, 0, true, null) },
            new() { Type = "batch", Batch = new([TestData.Record(2)], 0) },
            new() { Type = "error", Error = "Uncompressed diagnostic" }
        ];
        var captured = await CaptureAsync(messages, compression, legacy: false, server: false);
        int offset = 0;
        for (int i = 0; i < messages.Length; i++)
        {
            bool expectedCompressed = compression && messages[i].Type is "batch" or "current-response";
            Assert.Equal(expectedCompressed, (captured.Bytes[offset] & 0x40) != 0);
            offset += captured.Sizes[i];
        }
        using var stream = new MemoryStream(captured.Bytes);
        using var socket = WebSocket.CreateFromStream(stream, Options(compression, server: true));
        using var protocol = new WebSocketProtocol(socket);
        foreach (var expected in messages)
        {
            var actual = await protocol.ReceiveAsync(CancellationToken.None);
            Assert.Equal(JsonSerializer.Serialize(expected, ProtocolJson.Default.WireMessage),
                JsonSerializer.Serialize(actual, ProtocolJson.Default.WireMessage));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveLimitAppliesToExpandedMessage(bool compression)
    {
        using var outbound = new MemoryStream();
        using (var sender = WebSocket.CreateFromStream(outbound, Options(compression, server: false)))
        {
            var payload = Encoding.UTF8.GetBytes(new string('x', WebSocketProtocol.MaximumMessageBytes + 1));
            await sender.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, CancellationToken.None);
            using var inbound = new MemoryStream(outbound.ToArray());
            using var receiver = WebSocket.CreateFromStream(inbound, Options(compression, server: true));
            using var protocol = new WebSocketProtocol(receiver);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => protocol.ReceiveAsync(CancellationToken.None));
            Assert.Equal("Message too large.", error.Message);
        }
    }

    [Fact]
    public async Task RepresentativeHourUsesLessThanHalfTheOriginalWireBytes()
    {
        var start = DateTimeOffset.Parse("2026-11-12T08:00:00+00:00");
        var up = new List<WireMessage>();
        var down = new List<WireMessage>();
        for (int i = 1; i <= 720; i++)
        {
            var record = new TrafficRecord(123456 + i, start.AddSeconds((i - 1) * 5), start.AddSeconds(i * 5).AddTicks(1234567),
                5 + i % 13 * 0.0001234, 1000 + i * 37, 900 + i * 23, 3456.789012345678 + i * 29.1234567,
                4567.890123456789 + i * 17.1234567, false,
                new(start.AddDays(-30).AddTicks(1234567), 123456789012 + i * 10000L, 23456789012 + i * 9000L, false, null),
                [new("eth0", "eth0", 1000000000)]);
            up.Add(new() { Type = "batch", Batch = new([record], 0) });
            down.Add(new() { Type = "ack", AcknowledgedSequence = record.Sequence });
            if (i % 3 == 0)
            {
                up.Add(new() { Type = "ping" });
                down.Add(new() { Type = "pong" });
            }
        }
        long original = (await CaptureAsync(up, false, true, false)).Bytes.Length + (await CaptureAsync(down, false, true, true)).Bytes.Length;
        long compact = (await CaptureAsync(up, false, false, false)).Bytes.Length + (await CaptureAsync(down, false, false, true)).Bytes.Length;
        long compressed = (await CaptureAsync(up, true, false, false)).Bytes.Length + (await CaptureAsync(down, true, false, true)).Bytes.Length;
        output.WriteLine($"One hour, 720 uploads/acks and 240 ping/pongs, WebSocket frames only: original={original} B; compact={compact} B; compressed={compressed} B; saved={1 - (double)compressed / original:P1}.");
        output.WriteLine($"Per day: original={original * 24 / 1e6:F3} MB; compact={compact * 24 / 1e6:F3} MB; compressed={compressed * 24 / 1e6:F3} MB. Excludes TLS/TCP/IP, built-in keepalives, handshakes and retransmissions.");
        Assert.True(compressed < original / 2, $"Compressed {compressed} B; original {original} B.");
    }

    [Fact]
    public async Task CompressionCapableClientFallsBackWhenLegacyServerDoesNotNegotiate()
    {
        var builder = WebApplication.CreateSlimBuilder(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "None"]);
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/agent", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var protocol = new WebSocketProtocol(socket);
            var message = await protocol.ReceiveAsync(context.RequestAborted);
            await protocol.SendAsync(new WireMessage { Type = "ack", AcknowledgedSequence = message!.Batch!.Records[^1].Sequence }, context.RequestAborted);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
        });
        await app.StartAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var socket = new ClientWebSocket();
            socket.Options.DangerousDeflateOptions = new();
            socket.Options.CollectHttpResponseDetails = true;
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/agent" }.Uri, timeout.Token);
            Assert.False(socket.HttpResponseHeaders!.ContainsKey("Sec-WebSocket-Extensions"));
            using var protocol = new WebSocketProtocol(socket);
            await protocol.SendAsync(new WireMessage { Type = "batch", Batch = new([TestData.Record(1)], 0) }, timeout.Token);
            Assert.Equal(1, (await protocol.ReceiveAsync(timeout.Token))!.AcknowledgedSequence);
        }
        finally { await app.StopAsync(); }
    }

    private static WebSocketCreationOptions Options(bool compression, bool server) => new()
    {
        IsServer = server,
        KeepAliveInterval = Timeout.InfiniteTimeSpan,
        DangerousDeflateOptions = compression ? new WebSocketDeflateOptions() : null
    };

    private static async Task<(byte[] Bytes, int[] Sizes)> CaptureAsync(IEnumerable<WireMessage> messages, bool compression, bool legacy, bool server)
    {
        using var stream = new MemoryStream();
        using var socket = WebSocket.CreateFromStream(stream, Options(compression, server));
        using var protocol = new WebSocketProtocol(socket);
        var sizes = new List<int>();
        foreach (var message in messages)
        {
            long before = stream.Length;
            if (legacy)
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.WireMessage).AsMemory(),
                    WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, CancellationToken.None);
            else await protocol.SendAsync(message, CancellationToken.None);
            sizes.Add(checked((int)(stream.Length - before)));
        }
        return (stream.ToArray(), sizes.ToArray());
    }
}

public sealed class CompressionApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiveAcceptsLegacyAndCompressedClientsIncludingBacklogAndCurrent(bool compression)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        string id = compression ? "socket-1" : "socket-0";
        using var socket = new ClientWebSocket();
        socket.Options.DangerousDeflateOptions = compression ? new() : null;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + $"{id}-test-credential".PadRight(40, 'x'));
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri, token);
        Assert.Equal(compression, socket.HttpResponseHeaders!.ContainsKey("Sec-WebSocket-Extensions"));
        if (compression) Assert.Contains("permessage-deflate", string.Join(",", socket.HttpResponseHeaders["Sec-WebSocket-Extensions"]));
        using var protocol = new WebSocketProtocol(socket);
        await SendAsync(new WireMessage { Type = "hello", Hello = new(TestData.Node(id), 0, 0) });
        Assert.Equal("welcome", (await protocol.ReceiveAsync(token))!.Type);
        var batch = new WireMessage { Type = "batch", Batch = new(Enumerable.Range(1, 100).Select(n => TestData.Record(n, TestData.Now.AddSeconds((n - 100) * 5))).ToArray(), 0) };
        for (int replay = 0; replay < 2; replay++)
        {
            await SendAsync(batch);
            Assert.Equal(100, (await protocol.ReceiveAsync(token))!.AcknowledgedSequence);
        }
        await SendAsync(new WireMessage { Type = "ping" });
        Assert.Equal("pong", (await protocol.ReceiveAsync(token))!.Type);
        var total = JsonSerializer.Deserialize(await fixture.Client.GetStringAsync($"/api/v1/bees/{id}/traffic/total", token), ProtocolJson.Default.TotalResponse);
        Assert.Equal(15000, total!.Totals!.TotalBytes);
        var current = fixture.Client.GetAsync($"/api/v1/bees/{id}/traffic/current", token);
        var request = await protocol.ReceiveAsync(token);
        Assert.Equal("current-request", request!.Type);
        await SendAsync(new WireMessage { Type = "current-response", RequestId = request.RequestId,
            Current = new(DateTimeOffset.UtcNow, 1, 0, 800, true, null) });
        using var response = await current;
        response.EnsureSuccessStatusCode();
        Assert.Equal(800, JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(token), ProtocolJson.Default.CurrentTraffic)!.UploadBitsPerSecond);

        async Task SendAsync(WireMessage message)
        {
            if (compression) await protocol.SendAsync(message, token);
            else await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Default.WireMessage).AsMemory(), WebSocketMessageType.Text, true, token);
        }
    }
}
