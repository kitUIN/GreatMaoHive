namespace GreatMaoNetHive;

public sealed class ConnectionStatusService(HiveOptions options, BeeSessions sessions,
    ILogger<ConnectionStatusService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = options.Bees
                .Select(bee => new { bee.Id, Online = sessions.Get(bee.Id)?.Online == true })
                .OrderBy(bee => bee.Id, StringComparer.Ordinal)
                .ToArray();
            var online = snapshot.Where(bee => bee.Online).Select(bee => bee.Id).ToArray();
            var offline = snapshot.Where(bee => !bee.Online).Select(bee => bee.Id).ToArray();
            logger.LogInformation(
                "Hive connection status: {OnlineCount}/{TotalCount} Bees online; online: [{OnlineBeeIds}]; {OfflineCount} offline: [{OfflineBeeIds}]",
                online.Length, snapshot.Length, string.Join(", ", online), offline.Length, string.Join(", ", offline));
        }
    }
}
