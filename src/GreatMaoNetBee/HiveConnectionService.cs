using System.Net.WebSockets;
using System.Runtime.InteropServices;
using GreatMaoNet.Contracts;

namespace GreatMaoNetBee;

public sealed class HiveConnectionService(BeeOptions options, BeeStore store, SamplingService sampling,
    ILogger<HiveConnectionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try { await ConnectAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
            { logger.LogWarning("Hive connection interrupted: {Message}", exception.Message); }
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(60)) attempt = 0;
            double delay = Math.Min(60, Math.Pow(2, Math.Min(attempt++, 6))) + Random.Shared.NextDouble();
            await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
        }
    }

    private async Task ConnectAsync(CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri(options.HiveUrl), connectTimeout.Token);
        using var protocol = new WebSocketProtocol(socket);
        var checkpoint = store.Load();
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(
            new NodeInfo(options.Id, options.Name, RuntimeInformation.OSDescription, checkpoint.DataEpoch,
                checkpoint.Counters.Select(x => x.Interface).ToArray()) { Region = options.Region, Ip = options.Ip },
            store.Delivery().Ack, checkpoint.Sequence) }, stoppingToken);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        handshake.CancelAfter(TimeSpan.FromSeconds(15));
        var welcome = await protocol.ReceiveAsync(handshake.Token);
        if (welcome?.Type != "welcome") throw new InvalidDataException(welcome?.Error ?? "Hive refused handshake.");
        // A server with restored/empty storage must not silently acknowledge history already deleted locally.
        if (welcome.AcknowledgedSequence is not long initialAck || initialAck < store.Delivery().Ack)
            throw new InvalidDataException("Hive acknowledgement moved backwards; restore the Hive database.");
        store.Acknowledge(initialAck);
        logger.LogInformation("Connected to Hive as {BeeId}", options.Id);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        TaskCompletionSource<long>? pendingAck = null;
        var receive = ReceiveAsync();
        var upload = UploadAsync();
        var heartbeat = HeartbeatAsync();
        try { await await Task.WhenAny(receive, upload, heartbeat); }
        finally
        {
            await session.CancelAsync();
            socket.Abort();
            try { await Task.WhenAll(receive, upload, heartbeat); }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or TimeoutException) { }
        }

        async Task ReceiveAsync()
        {
            while (!session.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var message = await protocol.ReceiveAsync(timeout.Token) ?? throw new IOException("Hive closed connection.");
                switch (message.Type)
                {
                    case "ack" when message.AcknowledgedSequence is long ack:
                        store.Acknowledge(ack);
                        Volatile.Read(ref pendingAck)?.TrySetResult(ack);
                        break;
                    case "current-request" when message.RequestId is not null:
                        var current = await sampling.NextAsync(session.Token);
                        await protocol.SendAsync(new WireMessage { Type = "current-response", RequestId = message.RequestId, Current = current }, session.Token);
                        break;
                    case "pong": break;
                    case "error": throw new InvalidDataException(message.Error ?? "Protocol error.");
                    default: throw new InvalidDataException("Unexpected Hive message.");
                }
            }
        }
        async Task UploadAsync()
        {
            while (!session.IsCancellationRequested)
            {
                var batch = store.Pending();
                if (batch.Records.Length == 0) { await Task.Delay(500, session.Token); continue; }
                var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref pendingAck, completion);
                await protocol.SendAsync(new WireMessage { Type = "batch", Batch = batch }, session.Token);
                long ack = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), session.Token);
                if (ack < batch.Records[^1].Sequence) throw new InvalidDataException("Incomplete batch acknowledgement.");
            }
        }
        async Task HeartbeatAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(session.Token))
                await protocol.SendAsync(new WireMessage { Type = "ping" }, session.Token);
        }
    }
}
