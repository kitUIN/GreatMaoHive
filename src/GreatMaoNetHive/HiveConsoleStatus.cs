using GreatMaoNet.Updates;

namespace GreatMaoNetHive;

public sealed class HiveConsoleStatus(HiveOptions options, BeeSessions sessions) : IConsoleStatusProvider
{
    public IEnumerable<string> GetStatus()
    {
        var snapshot = sessions.Snapshot(options.Bees.Select(bee => bee.Id));
        yield return $"Hive connections: {snapshot.Count(bee => bee.Online)}/{snapshot.Length} Bees online";
        foreach (var bee in snapshot)
            yield return $"  {bee.Id}: {bee.Status}; last received on current connection (UTC): {bee.LastReceivedAt?.ToString("O") ?? "none"}";
    }
}
