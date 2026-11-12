using System.Net;
using System.Net.WebSockets;
using GreatMaoNet.Contracts;
using GreatMaoNetHive;
using Microsoft.Extensions.DependencyInjection;

namespace GreatMaoNet.Tests;

public sealed class ProtocolCloseTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtocolRejectionDeliversErrorThenCloseHandshake(bool afterHandshake)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        string id = afterHandshake ? "socket-60" : "socket-61";
        using var socket = await ConnectAsync(id, token);
        using var protocol = new WebSocketProtocol(socket);
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(TestData.Node(afterHandshake ? id : "wrong-node"), 0, 0) }, token);
        if (afterHandshake)
        {
            Assert.Equal("welcome", (await protocol.ReceiveAsync(token))!.Type);
            await protocol.SendAsync(new WireMessage { Type = "unexpected-command" }, token);
        }
        var error = await protocol.ReceiveAsync(token);
        Assert.Equal("error", error!.Type);
        Assert.Contains(afterHandshake ? "Unexpected Bee message" : "authenticated node ID", error.Error);
        Assert.Null(await protocol.ReceiveAsync(token));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.Equal(WebSocketState.CloseReceived, socket.State);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
        await WaitForCleanupAsync(id, token);
    }

    [Fact]
    public async Task PeerIgnoringCloseHandshakeIsStillRemovedAfterTimeout()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        const string id = "socket-62";
        using var socket = await ConnectAsync(id, token);
        using var protocol = new WebSocketProtocol(socket);
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(TestData.Node(id), 0, 0) }, token);
        Assert.Equal("welcome", (await protocol.ReceiveAsync(token))!.Type);
        await protocol.SendAsync(new WireMessage { Type = "unexpected-command" }, token);
        Assert.Equal("error", (await protocol.ReceiveAsync(token))!.Type);
        Assert.Null(await protocol.ReceiveAsync(token));
        // Deliberately do not acknowledge the close; the server must enforce its own deadline.
        await WaitForCleanupAsync(id, token);
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/healthz", token)).StatusCode);
    }

    private async Task<ClientWebSocket> ConnectAsync(string id, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + $"{id}-test-credential".PadRight(40, 'x'));
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri, token);
        return socket;
    }

    private async Task WaitForCleanupAsync(string id, CancellationToken token)
    {
        var sessions = fixture.Application.Services.GetRequiredService<BeeSessions>();
        while (sessions.Get(id) is not null) await Task.Delay(10, token);
    }
}
