using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using GreatMaoNet.Contracts;

namespace GreatMaoNetHive;

public sealed class BeeSessions(DatabaseWriter writer, ILogger<BeeSessions> logger)
{
    private readonly ConcurrentDictionary<string, BeeSession> sessions = new(StringComparer.Ordinal);
    public BeeSession? Get(string id) => sessions.GetValueOrDefault(id);

    public async Task AcceptAsync(HttpContext context, BeeCredential credential)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var protocol = new WebSocketProtocol(socket);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        BeeSession? session = null;
        string sessionId = Guid.NewGuid().ToString("N");
        bool registered = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await protocol.ReceiveAsync(timeout.Token);
            if (first?.Type != "hello" || first.Hello is null || first.Hello.Node.Id != credential.Id)
                throw new InvalidDataException("Expected hello with the authenticated node ID.");
            session = new BeeSession(protocol, socket, lifetime);
            if (sessions.TryGetValue(credential.Id, out var previous) && previous.Expired)
            {
                previous.Abort();
                ((ICollection<KeyValuePair<string, BeeSession>>)sessions).Remove(new(credential.Id, previous));
            }
            if (!sessions.TryAdd(credential.Id, session)) throw new InvalidDataException("This node already has an active connection.");
            long ack = await writer.RunAsync(store => store.Register(first.Hello, DateTimeOffset.UtcNow), lifetime.Token);
            // Record using a session-specific ID so an old connection's cleanup cannot close a replacement.
            // The queued write is awaited without request cancellation to avoid an orphaned interval.
            await writer.RunAsync(store => { store.OpenOnlineSession(sessionId, credential.Id, DateTimeOffset.UtcNow); return true; }, CancellationToken.None);
            registered = true;
            await protocol.SendAsync(new WireMessage { Type = "welcome", AcknowledgedSequence = ack }, lifetime.Token);
            session.Ready = true;
            while (!lifetime.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var message = await protocol.ReceiveAsync(readTimeout.Token);
                if (message is null) break;
                switch (message.Type)
                {
                    case "batch" when message.Batch is not null:
                        long committed = await writer.RunAsync(store => store.Ingest(credential.Id, message.Batch, DateTimeOffset.UtcNow), lifetime.Token);
                        await protocol.SendAsync(new WireMessage { Type = "ack", AcknowledgedSequence = committed }, lifetime.Token);
                        break;
                    case "ping":
                        await protocol.SendAsync(new WireMessage { Type = "pong" }, lifetime.Token);
                        break;
                    case "current-response" when message.Current is not null && message.RequestId is not null:
                        session.Deliver(message.RequestId, message.Current);
                        break;
                    default: throw new InvalidDataException("Unexpected Bee message.");
                }
                session.Touch();
                await writer.RunAsync(store => { store.TouchOnlineSession(sessionId, session.LastReceivedAt); return true; }, lifetime.Token);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or WebSocketException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning("Bee {BeeId} disconnected: {Message}", credential.Id, exception.Message);
            if (exception is InvalidDataException && socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await protocol.SendAsync(new WireMessage { Type = "error", Error = exception.Message }, timeout.Token); }
                catch (Exception sendException) when (sendException is WebSocketException or OperationCanceledException) { }
            }
        }
        finally
        {
            var disconnectedAt = DateTimeOffset.UtcNow;
            if (session is not null)
            {
                session.Abort();
                await session.DrainAsync();
                ((ICollection<KeyValuePair<string, BeeSession>>)sessions).Remove(new(credential.Id, session));
            }
            if (registered)
            {
                try { await writer.RunAsync(store => { store.CloseOnlineSession(sessionId, disconnectedAt); return true; }, CancellationToken.None); }
                catch (System.Threading.Channels.ChannelClosedException) { /* Host shutdown already closed observation intervals. */ }
            }
            socket.Abort();
        }
    }
}

public sealed class BeeSession(WebSocketProtocol protocol, WebSocket socket, CancellationTokenSource lifetime)
{
    private readonly object gate = new();
    private long lastTimestamp = Stopwatch.GetTimestamp();
    private long lastReceivedTicks = DateTimeOffset.UtcNow.UtcTicks;
    private Task<CurrentTraffic>? inFlight;
    private TaskCompletionSource<CurrentTraffic>? completion;
    private string? requestId;
    public bool Ready { get; set; }
    public bool Expired => lifetime.IsCancellationRequested || Stopwatch.GetElapsedTime(Interlocked.Read(ref lastTimestamp)).TotalSeconds >= 45;
    public bool Online => Ready && !Expired;
    public DateTimeOffset LastReceivedAt => new(Interlocked.Read(ref lastReceivedTicks), TimeSpan.Zero);

    public void Touch()
    {
        Interlocked.Exchange(ref lastTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref lastReceivedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public Task<CurrentTraffic> CurrentAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!Online) throw new IOException("Bee is offline.");
            if (inFlight is null || inFlight.IsCompleted) inFlight = RequestAsync();
            return inFlight.WaitAsync(cancellationToken);
        }
    }

    private async Task<CurrentTraffic> RequestAsync()
    {
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        requestId = Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await protocol.SendAsync(new WireMessage { Type = "current-request", RequestId = requestId }, timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        { throw new TimeoutException("Bee did not provide a new sample within three seconds."); }
    }

    public void Deliver(string id, CurrentTraffic current)
    {
        if (current.ObservedSeconds < 0 || !double.IsFinite(current.ObservedSeconds) ||
            current.DownloadBitsPerSecond is { } down && (!double.IsFinite(down) || down < 0) ||
            current.UploadBitsPerSecond is { } up && (!double.IsFinite(up) || up < 0))
            throw new InvalidDataException("Invalid current traffic sample.");
        lock (gate) { if (id == requestId) completion?.TrySetResult(current); }
    }

    public void Abort()
    {
        lifetime.Cancel();
        socket.Abort();
        lock (gate) completion?.TrySetException(new IOException("Bee disconnected."));
    }

    public async Task DrainAsync()
    {
        Task<CurrentTraffic>? pending;
        lock (gate) pending = inFlight;
        if (pending is null) return;
        try { await pending; }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException or WebSocketException) { }
    }
}
