using GreatMaoNet.Contracts;
using System.Text.Json.Serialization;

namespace GreatMaoNetBee;

public sealed record CounterReading(InterfaceInfo Interface, string Identity, long ReceivedBytes, long SentBytes);
public sealed class BeeCheckpoint
{
    public required string NodeId { get; init; }
    public string DataEpoch { get; init; } = Guid.NewGuid().ToString("N");
    public string BootId { get; set; } = "";
    public DateTimeOffset LastSampledAt { get; set; }
    public long Sequence { get; set; }
    public required TrafficTotals Totals { get; set; }
    public CounterReading[] Counters { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BeeCheckpoint))]
internal partial class BeeJson : JsonSerializerContext;

public sealed class SamplingEngine(BeeCheckpoint checkpoint)
{
    public BeeCheckpoint Checkpoint { get; } = checkpoint;
    private bool initialized;
    private double previousMonotonic;
    private long received;
    private long sent;
    private double seconds;
    private double peakDown;
    private double peakUp;
    private bool gap;

    public CurrentTraffic Advance(CounterReading[] readings, DateTimeOffset now, double monotonicSeconds, string bootId)
    {
        bool hadCheckpoint = Checkpoint.BootId.Length != 0;
        bool sameBoot = hadCheckpoint && Checkpoint.BootId == bootId;
        var previous = Checkpoint.Counters.ToDictionary(x => x.Identity, StringComparer.Ordinal);
        bool sameInterfaces = previous.Count == readings.Length && readings.All(x => previous.ContainsKey(x.Identity));
        double elapsed = initialized ? monotonicSeconds - previousMonotonic : 0;
        bool continuous = initialized && sameBoot && sameInterfaces && readings.Length > 0 && elapsed > 0 && elapsed <= 3;
        string? reason = null;
        if (readings.Length == 0) reason = "no-interface";
        else if (hadCheckpoint && !sameBoot) reason = "system-restarted";
        else if (hadCheckpoint && !sameInterfaces) reason = "interface-changed";
        else if (!initialized && hadCheckpoint) reason = "process-restarted";
        else if (initialized && (elapsed <= 0 || elapsed > 3)) reason = "sampling-delayed";
        else if (initialized && Math.Abs((now - Checkpoint.LastSampledAt).TotalSeconds - elapsed) > 2)
        {
            reason = "clock-adjusted";
            continuous = false;
            // Bytes remain in lifetime totals; their old clock bucket is no longer trustworthy.
            received = sent = 0;
            seconds = peakDown = peakUp = 0;
        }

        long deltaReceived = 0, deltaSent = 0;
        if (sameBoot)
        {
            foreach (var reading in readings)
            {
                if (!previous.TryGetValue(reading.Identity, out var old)) continue;
                if (reading.ReceivedBytes < old.ReceivedBytes || reading.SentBytes < old.SentBytes)
                {
                    continuous = false;
                    reason = "counter-reset";
                    continue;
                }
                deltaReceived = checked(deltaReceived + reading.ReceivedBytes - old.ReceivedBytes);
                deltaSent = checked(deltaSent + reading.SentBytes - old.SentBytes);
            }
        }

        bool hasGap = reason is not null;
        gap |= hasGap;
        Checkpoint.Totals = Checkpoint.Totals with
        {
            ReceivedBytes = checked(Checkpoint.Totals.ReceivedBytes + deltaReceived),
            SentBytes = checked(Checkpoint.Totals.SentBytes + deltaSent),
            HasGaps = Checkpoint.Totals.HasGaps || hasGap,
            LastGapAt = hasGap ? now : Checkpoint.Totals.LastGapAt
        };
        double? down = continuous ? deltaReceived * 8.0 / elapsed : null;
        double? up = continuous ? deltaSent * 8.0 / elapsed : null;
        if (continuous)
        {
            received = checked(received + deltaReceived);
            sent = checked(sent + deltaSent);
            seconds += elapsed;
            peakDown = Math.Max(peakDown, down!.Value);
            peakUp = Math.Max(peakUp, up!.Value);
        }
        Checkpoint.BootId = bootId;
        Checkpoint.Counters = readings;
        Checkpoint.LastSampledAt = now;
        previousMonotonic = monotonicSeconds;
        initialized = true;
        return new CurrentTraffic(now, continuous ? elapsed : 0, down, up, continuous, reason);
    }

    public TrafficRecord CompleteRecord(DateTimeOffset bucketStart)
    {
        var record = new TrafficRecord(++Checkpoint.Sequence, bucketStart, Checkpoint.LastSampledAt, seconds, received, sent,
            peakDown, peakUp, gap || seconds < 4, Checkpoint.Totals, Checkpoint.Counters.Select(x => x.Interface).ToArray());
        received = sent = 0;
        seconds = peakDown = peakUp = 0;
        gap = false;
        return record;
    }
}
