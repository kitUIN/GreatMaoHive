using System.IO.Pipes;
using System.Net.WebSockets;
using GreatMaoNet.Contracts;
using GreatMaoNetHive;
using Microsoft.Extensions.DependencyInjection;

namespace GreatMaoNet.Tests;

public sealed class HiveBatchControlTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task NoIdUpdatesAllOnlineBeesAndStreamsResultsLargerThanOneFrame()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var first = await ConnectAsync("socket-80", token);
        await using var second = await ConnectAsync("socket-81", token);
        using var directory = new TemporaryDirectory();
        var firstReply = ReplyAsync(first, "accepted", token);
        var secondReply = ReplyAsync(second, "accepted", token);
        var result = await HiveControlTests.RunProcessAsync(
            ["--update-bee", "--Hive:ControlPipeName", fixture.ControlPipeName], directory.Path, token);
        await Task.WhenAll(firstReply, secondReply);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("Bee socket-80: update accepted", result.Output);
        Assert.Contains("Bee socket-81: update accepted", result.Output);
        Assert.Contains("2 accepted, 0 failed, 99 offline skipped", result.Output);
        Assert.Equal(99, result.Output.Split('\n').Count(line => line.Contains("offline; skipped.", StringComparison.Ordinal)));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
        Assert.Equal("pong", await PingAsync(first, token));
        Assert.Equal("pong", await PingAsync(second, token));
    }

    [Fact]
    public async Task RejectionsTimeoutAndLegacyNodeDoNotPreventLaterNodesUpdating()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var rejected = await ConnectAsync("socket-82", token);
        await using var silent = await ConnectAsync("socket-83", token);
        await using var legacy = await ConnectAsync("socket-84", token, capable: false);
        await using var accepted = await ConnectAsync("socket-85", token);
        var rejectedReply = ReplyAsync(rejected, "disabled", token);
        var silentRequest = silent.Protocol.ReceiveAsync(token);
        var acceptedReply = ReplyAsync(accepted, "accepted", token);
        using var directory = new TemporaryDirectory();
        var result = await HiveControlTests.RunProcessAsync(
            ["--Hive:ControlPipeName", fixture.ControlPipeName, "--update-bee"], directory.Path, token);
        await Task.WhenAll(rejectedReply, acceptedReply);
        Assert.Equal("update-request", (await silentRequest)!.Type);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("1 accepted, 3 failed, 97 offline skipped", result.Output);
        Assert.Contains("Bee socket-82: update request failed (409)", result.Error);
        Assert.Contains("disabled", result.Error);
        Assert.Contains("Bee socket-83: update request failed (504)", result.Error);
        Assert.Contains("Bee socket-84: update request failed (409)", result.Error);
        Assert.Equal("pong", await PingAsync(legacy, token));
    }

    [Fact]
    public async Task BatchCanExceedOldTenSecondDeadlineWhileEachNodeRespondsInTime()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var first = await ConnectAsync("socket-86", token);
        await using var second = await ConnectAsync("socket-87", token);
        await using var third = await ConnectAsync("socket-88", token);
        var replies = Task.WhenAll(ReplyAsync(first, "accepted", token, delay: 3600),
            ReplyAsync(second, "accepted", token, delay: 3600), ReplyAsync(third, "accepted", token, delay: 3600));
        using var directory = new TemporaryDirectory();
        var result = await HiveControlTests.RunProcessAsync(
            ["--update-bee", "--Hive:ControlPipeName=" + fixture.ControlPipeName], directory.Path, token);
        await replies;
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("3 accepted, 0 failed, 98 offline skipped", result.Output);
    }

    [Fact]
    public async Task AllOfflineReportsNoWorkAndNonzeroExit()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await HiveControlTests.RunProcessAsync(
            ["--update-bee", "--Hive:ControlPipeName", fixture.ControlPipeName], directory.Path, timeout.Token);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("0 accepted, 0 failed, 101 offline skipped", result.Output);
        Assert.Contains("No online Bees", result.Error);
    }

    [Fact]
    public async Task OfflineNodeConnectingMidBatchIsSkippedUntilAnotherInvocation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        await using var first = await ConnectAsync("socket-89", token);
        using var directory = new TemporaryDirectory();
        var process = HiveControlTests.RunProcessAsync(
            ["--update-bee", "--Hive:ControlPipeName", fixture.ControlPipeName], directory.Path, token);
        var request = await first.Protocol.ReceiveAsync(token);
        await using var newcomer = await ConnectAsync("socket-90", token);
        await first.Protocol.SendAsync(new WireMessage { Type = "update-response", RequestId = request!.RequestId, UpdateStatus = "accepted" }, token);
        var result = await process;
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Bee socket-90: offline; skipped.", result.Output);
        Assert.Contains("1 accepted, 0 failed, 100 offline skipped", result.Output);
        Assert.Equal("pong", await PingAsync(newcomer, token));
    }

    [Fact]
    public async Task OldHiveReplyReportsUpgradeRequirementInsteadOfClaimingBatchSucceeded()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        string pipe = OperatingSystem.IsWindows() ? "gm-old-" + Guid.NewGuid().ToString("N")
            : Path.Combine(Path.GetTempPath(), "gm-old-" + Guid.NewGuid().ToString("N") + ".sock");
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = HiveControlCommand.HandleAsync(["--update-bee", "--Hive:ControlPipeName", pipe], output, error, token);
        await server.WaitForConnectionAsync(token);
        var request = await HiveControlProtocol.ReadAsync(server, HiveControlJson.Default.HiveControlRequest, token);
        Assert.Equal("update-all-bees", request.Command);
        Assert.Null(request.BeeId);
        await HiveControlProtocol.WriteAsync(server, new BeeUpdateCommandResult(400, null, "Expected update-bee and a valid Bee ID."), HiveControlJson.Default.BeeUpdateCommandResult, token);
        await server.ReadExactlyAsync(new byte[1], token);
        Assert.Equal(1, await command);
        Assert.Contains("upgrade and restart", error.ToString());
        Assert.DoesNotContain("Batch complete", output.ToString());
    }

    private async Task<Peer> ConnectAsync(string id, CancellationToken token, bool capable = true)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + $"{id}-test-credential".PadRight(40, 'x'));
        await socket.ConnectAsync(new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri, token);
        var protocol = new WebSocketProtocol(socket);
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(TestData.Node(id), 0, 0),
            Capabilities = capable ? ["remote-update-v1"] : null }, token);
        Assert.Equal("welcome", (await protocol.ReceiveAsync(token))!.Type);
        var peer = new Peer(id, socket, protocol, fixture.Application.Services.GetRequiredService<BeeSessions>());
        Assert.Equal("pong", await PingAsync(peer, token));
        return peer;
    }

    private static async Task ReplyAsync(Peer peer, string status, CancellationToken token, int delay = 0)
    {
        var request = await peer.Protocol.ReceiveAsync(token);
        Assert.Equal("update-request", request!.Type);
        if (delay > 0) await Task.Delay(delay, token);
        await peer.Protocol.SendAsync(new WireMessage { Type = "update-response", RequestId = request.RequestId, UpdateStatus = status }, token);
    }

    private static async Task<string> PingAsync(Peer peer, CancellationToken token)
    {
        await peer.Protocol.SendAsync(new WireMessage { Type = "ping" }, token);
        return (await peer.Protocol.ReceiveAsync(token))!.Type;
    }

    private sealed record Peer(string Id, ClientWebSocket Socket, WebSocketProtocol Protocol, BeeSessions Sessions) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Socket.Abort();
            Protocol.Dispose();
            Socket.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Sessions.Get(Id) is not null) await Task.Delay(10, timeout.Token);
        }
    }
}
