namespace GreatMaoNet.Updates;

public interface IRemoteUpdateTrigger
{
    string RequestUpdate();
}

// Automatic and remote updates share one worker and one reservation.
public sealed class UpdateRequestQueue(UpdateOptions options) : IRemoteUpdateTrigger, IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly object gate = new();
    private bool available;
    private bool busy;

    public void SetAvailable(bool value) { lock (gate) available = value; }

    public string RequestUpdate()
    {
        lock (gate)
        {
            if (!options.AllowRemoteUpdates || !options.AutoDownload || !options.AutoApply) return "disabled";
            if (!available) return "unavailable";
            if (busy) return "busy";
            busy = true;
            signal.Release();
            return "accepted";
        }
    }

    public Task<bool> WaitAsync(TimeSpan delay, CancellationToken token) => signal.WaitAsync(delay, token);
    public bool TryBeginAutomatic()
    {
        lock (gate)
        {
            if (busy) return false;
            busy = true;
            return true;
        }
    }
    public void Complete() { lock (gate) busy = false; }
    public void Dispose() => signal.Dispose();
}
