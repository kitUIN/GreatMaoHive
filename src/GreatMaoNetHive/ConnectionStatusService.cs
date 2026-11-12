namespace GreatMaoNetHive;

public sealed class ConnectionStatusService(HiveOptions options, BeeSessions sessions,
    ILogger<ConnectionStatusService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = sessions.Snapshot(options.Bees.Select(bee => bee.Id));
            var online = snapshot.Where(bee => bee.Status == "online").Select(bee => bee.Id).ToArray();
            var offline = snapshot.Where(bee => bee.Status == "offline").Select(bee => bee.Id).ToArray();
            var upgrading = snapshot.Where(bee => bee.Upgrading).Select(bee => bee.Id).ToArray();
            logger.LogInformation(
                "Hive connection status: {OnlineCount}/{TotalCount} Bees online; online: [{OnlineBeeIds}]; {OfflineCount} offline: [{OfflineBeeIds}]; {UpgradingCount} upgrading: [{UpgradingBeeIds}]",
                online.Length, snapshot.Length, string.Join(", ", online), offline.Length, string.Join(", ", offline), upgrading.Length, string.Join(", ", upgrading));
        }
    }
}
