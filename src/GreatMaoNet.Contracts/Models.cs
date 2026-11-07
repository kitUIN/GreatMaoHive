using System.Text.Json.Serialization;

namespace GreatMaoNet.Contracts;

public sealed record InterfaceInfo(string Id, string Name, long? LinkSpeedBitsPerSecond);
public sealed record NodeInfo(string Id, string Name, string OperatingSystem, string DataEpoch, InterfaceInfo[] Interfaces)
{
    public string? Region { get; init; }
    public string? Ip { get; init; }
}
public sealed record TrafficTotals(DateTimeOffset StartedAt, long ReceivedBytes, long SentBytes,
    bool HasGaps, DateTimeOffset? LastGapAt)
{
    public long TotalBytes => checked(ReceivedBytes + SentBytes);
}

public sealed record CurrentTraffic(DateTimeOffset SampledAt, double ObservedSeconds,
    double? DownloadBitsPerSecond, double? UploadBitsPerSecond, bool Available, string? GapReason);

public sealed record TrafficRecord(long Sequence, DateTimeOffset BucketStart, DateTimeOffset SampledAt, double ObservedSeconds,
    long ReceivedBytes, long SentBytes, double PeakDownloadBitsPerSecond, double PeakUploadBitsPerSecond,
    bool HasGap, TrafficTotals Totals, InterfaceInfo[] Interfaces);

public sealed record HelloMessage(NodeInfo Node, long LastAcknowledgedSequence, long LastSequence);
public sealed record BatchMessage(TrafficRecord[] Records, long DiscardedThroughSequence);
public sealed record WireMessage
{
    public int Version { get; init; } = 1;
    public required string Type { get; init; }
    public HelloMessage? Hello { get; init; }
    public BatchMessage? Batch { get; init; }
    public long? AcknowledgedSequence { get; init; }
    public string? RequestId { get; init; }
    public CurrentTraffic? Current { get; init; }
    public string? Error { get; init; }
}

public sealed record BeeSummary(string Id, string Name, bool Online, DateTimeOffset? LastSampledAt,
    DateTimeOffset? LastReceivedAt, TrafficTotals? Totals)
{
    public string? Region { get; init; }
    public string? Ip { get; init; }
}
public sealed record BeeDetail(NodeInfo Node, bool Online, DateTimeOffset? LastSampledAt,
    DateTimeOffset? LastReceivedAt, TrafficTotals? Totals);
public sealed record TotalResponse(TrafficTotals? Totals, bool Online, DateTimeOffset? LastSampledAt,
    DateTimeOffset? LastReceivedAt);

public sealed record HistoryPoint(DateTimeOffset Start, DateTimeOffset End, long? ReceivedBytes,
    long? SentBytes, double? DownloadBitsPerSecond, double? UploadBitsPerSecond,
    double? PeakDownloadBitsPerSecond, double? PeakUploadBitsPerSecond, double CoverageSeconds,
    bool HasGap, InterfaceInfo[] Interfaces);
public sealed record HistoryResponse(string Step, DateTimeOffset From, DateTimeOffset To, HistoryPoint[] Points);
public sealed record HealthResponse(string Status);

public sealed record TrafficQuotaPolicy(long? LimitBytes, string Period, int ResetDay, int UtcOffsetHours);
public sealed record BeeTrafficQuota(string Id, string? Name, bool Online, TrafficQuotaPolicy Quota,
    DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, long? ReceivedBytes, long? SentBytes,
    long? TotalBytes, long? RemainingBytes, bool? IsExceeded, bool HasGaps,
    DateTimeOffset? LastSampledAt, DateTimeOffset? LastReceivedAt)
{
    public string? Region { get; init; }
    public string? Ip { get; init; }
}
public sealed record TrafficQuotasResponse(DateTimeOffset AsOf, BeeTrafficQuota[] Bees);
public sealed record OnlineInterval(DateTimeOffset From, DateTimeOffset To, string Status);
public sealed record OnlineHistoryResponse(string Id, DateTimeOffset From, DateTimeOffset To, DateTimeOffset AsOf,
    double OnlineSeconds, double OfflineSeconds, double UnknownSeconds, OnlineInterval[] Intervals);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WireMessage))]
[JsonSerializable(typeof(NodeInfo))]
[JsonSerializable(typeof(TrafficRecord))]
[JsonSerializable(typeof(TrafficTotals))]
[JsonSerializable(typeof(InterfaceInfo[]))]
[JsonSerializable(typeof(BeeSummary[]))]
[JsonSerializable(typeof(BeeDetail))]
[JsonSerializable(typeof(TotalResponse))]
[JsonSerializable(typeof(CurrentTraffic))]
[JsonSerializable(typeof(HistoryResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(TrafficQuotasResponse))]
[JsonSerializable(typeof(OnlineHistoryResponse))]
public partial class ProtocolJson : JsonSerializerContext;
