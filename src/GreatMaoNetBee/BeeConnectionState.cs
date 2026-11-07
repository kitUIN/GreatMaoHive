namespace GreatMaoNetBee;

public sealed class BeeConnectionState(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private string state = "Disconnected";
    private DateTimeOffset? connectedAt;
    private DateTimeOffset? lastReceivedAt;
    private long lastReceivedTimestamp;

    public void Connecting() { lock (gate) state = "Connecting"; }
    public void Disconnected() { lock (gate) state = "Disconnected"; }
    public void Connected()
    {
        lock (gate)
        {
            state = "Connected";
            connectedAt = lastReceivedAt = clock.GetUtcNow();
            lastReceivedTimestamp = clock.GetTimestamp();
        }
    }
    public void Received()
    {
        lock (gate)
        {
            lastReceivedAt = clock.GetUtcNow();
            lastReceivedTimestamp = clock.GetTimestamp();
        }
    }
    public BeeConnectionSnapshot Snapshot()
    {
        lock (gate)
        {
            bool expired = state == "Connected" && clock.GetElapsedTime(lastReceivedTimestamp) >= TimeSpan.FromSeconds(45);
            return new(expired ? "Timed out" : state, connectedAt, lastReceivedAt);
        }
    }
}

public sealed record BeeConnectionSnapshot(string State, DateTimeOffset? LastConnectedAt, DateTimeOffset? LastReceivedAt);
