using System.Diagnostics;
using GreatMaoNet.Contracts;

namespace GreatMaoNetBee;

public sealed class SamplingService(BeeStore store, ICounterSource source, ILogger<SamplingService> logger) : BackgroundService
{
    private readonly object gate = new();
    private TaskCompletionSource<CurrentTraffic> nextSample = NewCompletion();
    private static TaskCompletionSource<CurrentTraffic> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CurrentTraffic> NextAsync(CancellationToken cancellationToken)
    {
        lock (gate) return nextSample.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var engine = new SamplingEngine(store.Load());
        var clock = Stopwatch.StartNew();
        double lastCommit = 0;
        var bucket = Bucket(DateTimeOffset.UtcNow);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                var now = DateTimeOffset.UtcNow;
                double elapsed = clock.Elapsed.TotalSeconds;
                CounterReading[] readings;
                try { readings = source.Read(elapsed); }
                catch (Exception exception) when (exception is IOException or System.Net.NetworkInformation.NetworkInformationException)
                {
                    logger.LogWarning("Network sampling unavailable: {Message}", exception.Message);
                    readings = [];
                }
                var current = engine.Advance(readings, now, elapsed, source.BootId);
                if (current.GapReason == "clock-adjusted") bucket = Bucket(now);
                lock (gate)
                {
                    nextSample.TrySetResult(current);
                    nextSample = NewCompletion();
                }
                var nextBucket = Bucket(now);
                if (nextBucket != bucket || elapsed - lastCommit >= 5.5)
                {
                    Commit(engine, bucket, now);
                    bucket = nextBucket;
                    lastCommit = elapsed;
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (clock.Elapsed.TotalSeconds - lastCommit > 0.001) Commit(engine, bucket, DateTimeOffset.UtcNow);
            lock (gate) nextSample.TrySetCanceled(stoppingToken);
        }
    }

    private static DateTimeOffset Bucket(DateTimeOffset now) => DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 5 * 5);

    private void Commit(SamplingEngine engine, DateTimeOffset bucket, DateTimeOffset now)
    {
        store.Commit(engine.Checkpoint, engine.CompleteRecord(bucket), now);
    }
}
