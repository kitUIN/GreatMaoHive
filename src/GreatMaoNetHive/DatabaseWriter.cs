using System.Threading.Channels;

namespace GreatMaoNetHive;

public sealed class DatabaseWriter(HiveStore store) : BackgroundService
{
    private readonly Channel<Action> queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(256)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public async Task<T> RunAsync<T>(Func<HiveStore, T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(() =>
        {
            try { completion.TrySetResult(action(store)); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once enqueued, writes finish even if a WebSocket disconnects. The sequence makes replay safe.
        await foreach (var action in queue.Reader.ReadAllAsync()) action();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}

public sealed class RetentionService(DatabaseWriter writer, HiveOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do { await writer.RunAsync(store =>
        {
            var now = DateTimeOffset.UtcNow;
            store.Prune(now);
            store.PruneOnlineHistory(now.AddDays(-options.OnlineHistoryRetentionDays));
            return true;
        }, stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
