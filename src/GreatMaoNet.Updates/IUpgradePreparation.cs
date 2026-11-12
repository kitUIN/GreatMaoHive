namespace GreatMaoNet.Updates;

// Called only after staging/validation and updater readiness, immediately before shutdown.
public interface IUpgradePreparation
{
    Task PrepareAsync(CancellationToken token);
    Task CancelAsync(CancellationToken token);
}
