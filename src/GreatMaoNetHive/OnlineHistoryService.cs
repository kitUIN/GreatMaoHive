namespace GreatMaoNetHive;

public sealed class OnlineHistoryService(DatabaseWriter writer, HiveOptions options) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await writer.RunAsync(store => { store.StartObservation(options.Bees.Select(x => x.Id), DateTimeOffset.UtcNow); return true; }, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await writer.RunAsync(store => { store.Observe(DateTimeOffset.UtcNow); return true; }, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await writer.RunAsync(store => { store.StopObservation(DateTimeOffset.UtcNow); return true; }, cancellationToken);
    }
}
