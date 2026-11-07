using GreatMaoNet.Updates;

namespace GreatMaoNetBee;

public sealed class BeeConsoleStatus(BeeOptions options, BeeConnectionState connection) : IConsoleStatusProvider
{
    public IEnumerable<string> GetStatus()
    {
        var status = connection.Snapshot();
        yield return $"Bee {options.Id} ({options.Name})";
        yield return $"Hive connection: {status.State}";
        yield return $"Last connected (UTC): {status.LastConnectedAt?.ToString("O") ?? "never"}";
        yield return $"Last received (UTC): {status.LastReceivedAt?.ToString("O") ?? "never"}";
    }
}
