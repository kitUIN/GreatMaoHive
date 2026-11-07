using GreatMaoNet.Contracts;

namespace GreatMaoNetHive;

public sealed partial class HiveStore
{
    public TrafficQuotasResponse TrafficQuotas(BeeCredential[] configuredBees, DateTimeOffset now)
    {
        using var connection = OpenReader();
        // One read snapshot for both checkpoints and buckets, even during concurrent uploads.
        using var transaction = connection.BeginTransaction(deferred: true);
        var nodes = new Dictionary<string, StoredBee>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT node_json,seq,totals_json,sampled,received FROM nodes";
            using var reader = command.ExecuteReader();
            while (reader.Read()) { var node = ReadBee(reader); nodes.Add(node.Node.Id, node); }
        }
        var results = new List<BeeTrafficQuota>();
        foreach (var bee in configuredBees.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var policy = bee.Quota;
            var (start, end) = policy.GetPeriod(now);
            var node = nodes.GetValueOrDefault(bee.Id);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Whole-hour offsets align billing boundaries with the durable hourly rollups.
            // Never add multiple resolutions, or the same traffic would be counted three times.
            command.CommandText = """
                SELECT SUM(rx),SUM(tx),SUM(MIN(seconds,3600)),MAX(gap)
                FROM buckets WHERE node=$id AND step=3600 AND start >= $from AND start < $to
                """;
            command.Parameters.AddWithValue("$id", bee.Id);
            command.Parameters.AddWithValue("$from", start.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$to", Math.Min(end.ToUnixTimeSeconds(), HistoryQuery.Floor(now.ToUnixTimeSeconds(), 3600) + 3600));
            using var reader = command.ExecuteReader();
            reader.Read();
            long? received = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            long? sent = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            long? total = received is null || sent is null ? null : checked(received.Value + sent.Value);
            double coverage = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
            var lastSample = node?.LastSampledAt;
            var through = lastSample is { } sampled && sampled < now ? sampled : now;
            bool gaps = total is null || (!reader.IsDBNull(3) && reader.GetBoolean(3)) ||
                node?.Totals is not { } totals || totals.StartedAt > start ||
                (totals.HasGaps && (totals.LastGapAt is null || totals.LastGapAt >= start)) ||
                coverage + 0.25 < Math.Max(0, (through - start).TotalSeconds);
            long? remaining = policy.LimitBytes is { } limit && total is { } usage ? Math.Max(0, limit - usage) : null;
            bool? exceeded = policy.LimitBytes is { } ceiling && total is { } used ? used > ceiling : null;
            results.Add(new(bee.Id, node?.Node.Name, false,
                new(policy.LimitBytes, policy.Period, policy.ResetDay, policy.UtcOffsetHours),
                start, end, received, sent, total, remaining, exceeded, gaps, lastSample, node?.LastReceivedAt)
                { Region = node?.Node.Region, Ip = node?.Node.Ip });
        }
        transaction.Commit();
        return new(now.ToUniversalTime(), results.ToArray());
    }
}
