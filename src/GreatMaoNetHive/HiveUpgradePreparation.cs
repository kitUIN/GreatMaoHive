using GreatMaoNet.Updates;

namespace GreatMaoNetHive;

public sealed class HiveUpgradePreparation(DatabaseWriter writer, HiveOptions options) : IUpgradePreparation
{
    private string? operation;

    public async Task PrepareAsync(CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        operation = id;
        await writer.RunAsync(store => { store.BeginHiveUpgrade(id, DateTimeOffset.UtcNow, options.UpgradeGraceSeconds); return true; }, token);
    }

    public async Task CancelAsync(CancellationToken token)
    {
        if (operation is not { } id) return;
        await writer.RunAsync(store => { store.CancelHiveUpgrade(id); return true; }, token);
        operation = null;
    }
}
