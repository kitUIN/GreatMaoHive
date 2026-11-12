using System.Net.WebSockets;
using System.Runtime.InteropServices;
using GreatMaoNet.Contracts;
using GreatMaoNet.Updates;

namespace GreatMaoNetBee;

public sealed class HiveConnectionService(BeeOptions options, BeeStore store, SamplingService sampling, BeeConnectionState connection,
    ILogger<HiveConnectionService> logger, IRemoteUpdateTrigger? updates = null) : BackgroundService, IUpgradePreparation
{
    private Func<bool, CancellationToken, Task>? upgradeNotice;

    public Task PrepareAsync(CancellationToken token) => NotifyUpgradeAsync(true, token);
    public Task CancelAsync(CancellationToken token) => NotifyUpgradeAsync(false, token);

    private Task NotifyUpgradeAsync(bool starting, CancellationToken token)
    {
        var notify = Volatile.Read(ref upgradeNotice);
        if (notify is not null) return notify(starting, token);
        if (starting) logger.LogWarning("Hive is disconnected or does not support upgrade notices; this upgrade cannot be excluded from its offline history.");
        return Task.CompletedTask;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            connection.Connecting();
            try { await ConnectAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
            { logger.LogWarning("Hive connection interrupted: {Message}", exception.Message); }
            finally { connection.Disconnected(); }
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(60)) attempt = 0;
            double delay = Math.Min(60, Math.Pow(2, Math.Min(attempt++, 6))) + Random.Shared.NextDouble();
            await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
        }
    }

    private async Task ConnectAsync(CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        await socket.ConnectAsync(new Uri(options.HiveUrl), connectTimeout.Token);
        using var protocol = new WebSocketProtocol(socket);
        var checkpoint = store.Load();
        await protocol.SendAsync(new WireMessage { Type = "hello", Hello = new(
            new NodeInfo(options.Id, options.Name, RuntimeInformation.OSDescription, checkpoint.DataEpoch,
                checkpoint.Counters.Select(x => x.Interface).ToArray()) { Region = options.Region, Ip = options.Ip },
            store.Delivery().Ack, checkpoint.Sequence), Capabilities = updates is null ? null : ["remote-update-v1"] }, stoppingToken);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        handshake.CancelAfter(TimeSpan.FromSeconds(15));
        var welcome = await protocol.ReceiveAsync(handshake.Token);
        if (welcome?.Type != "welcome") throw new InvalidDataException(welcome?.Error ?? "Hive refused handshake.");
        // A server with restored/empty storage must not silently acknowledge history already deleted locally.
        if (welcome.AcknowledgedSequence is not long initialAck || initialAck < store.Delivery().Ack)
            throw new InvalidDataException("Hive acknowledgement moved backwards; restore the Hive database.");
        store.Acknowledge(initialAck);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        TaskCompletionSource<long>? pendingAck = null;
        (string Id, string Type, TaskCompletionSource Completion)? pendingUpgrade = null;
        var upgradeGate = new object();
        string? upgradeId = null;
        Func<bool, CancellationToken, Task> notifyUpgrade = SendUpgradeNoticeAsync;
        if (welcome.Capabilities?.Contains("upgrade-notice-v1", StringComparer.Ordinal) == true)
            Volatile.Write(ref upgradeNotice, notifyUpgrade);
        connection.Connected();
        logger.LogInformation("Connected to Hive as {BeeId}", options.Id);
        var receive = ReceiveAsync();
        var upload = UploadAsync();
        var heartbeat = HeartbeatAsync();
        try { await await Task.WhenAny(receive, upload, heartbeat); }
        finally
        {
            Interlocked.CompareExchange(ref upgradeNotice, null, notifyUpgrade);
            lock (upgradeGate) pendingUpgrade?.Completion.TrySetException(new IOException("Hive disconnected before acknowledging the upgrade notice."));
            connection.Disconnected();
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
                connection.Received();
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
                    case "update-request" when Guid.TryParseExact(message.RequestId, "N", out _):
                        string status = updates?.RequestUpdate() ?? "unavailable";
                        await protocol.SendAsync(new WireMessage { Type = "update-response", RequestId = message.RequestId,
                            UpdateStatus = status }, session.Token);
                        logger.LogInformation("Hive update request {RequestId}: {Status}", message.RequestId, status);
                        break;
                    case "upgrade-ready" or "upgrade-cancelled":
                        lock (upgradeGate)
                            if (pendingUpgrade is { } pending && pending.Id == message.RequestId && pending.Type == message.Type)
                                pending.Completion.TrySetResult();
                        break;
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

        async Task SendUpgradeNoticeAsync(bool starting, CancellationToken token)
        {
            if (starting) upgradeId = Guid.NewGuid().ToString("N");
            if (upgradeId is not { } id) return;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (upgradeGate) pendingUpgrade = (id, starting ? "upgrade-ready" : "upgrade-cancelled", completion);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await protocol.SendAsync(new WireMessage { Type = starting ? "upgrade-start" : "upgrade-cancel", RequestId = id }, timeout.Token);
                await completion.Task.WaitAsync(timeout.Token);
                if (!starting) upgradeId = null;
            }
            finally { lock (upgradeGate) pendingUpgrade = null; }
        }
    }
}
