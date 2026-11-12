using System.Text.Json;
using GreatMaoNet.Contracts;
using Microsoft.Data.Sqlite;

namespace GreatMaoNetHive;

public sealed record StoredBee(NodeInfo Node, long Sequence, TrafficTotals? Totals,
    DateTimeOffset? LastSampledAt, DateTimeOffset? LastReceivedAt);

public sealed partial class HiveStore : IDisposable
{
    private readonly SqliteConnection writer;
    private readonly string connectionString;

    public HiveStore(HiveOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, DefaultTimeout = 5 }.ToString();
        writer = new SqliteConnection(connectionString);
        writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS nodes(
                id TEXT PRIMARY KEY, node_json TEXT NOT NULL, epoch TEXT NOT NULL, seq INTEGER NOT NULL DEFAULT 0,
                totals_json TEXT, sampled INTEGER, received INTEGER);
            CREATE TABLE IF NOT EXISTS buckets(
                node TEXT NOT NULL REFERENCES nodes(id), step INTEGER NOT NULL, start INTEGER NOT NULL,
                rx INTEGER NOT NULL, tx INTEGER NOT NULL, seconds REAL NOT NULL,
                peak_rx REAL NOT NULL, peak_tx REAL NOT NULL, gap INTEGER NOT NULL, interfaces TEXT NOT NULL,
                PRIMARY KEY(node,step,start));
            CREATE INDEX IF NOT EXISTS ix_buckets_retention ON buckets(step,start);
            CREATE TABLE IF NOT EXISTS observation_runs(
                id TEXT PRIMARY KEY, start INTEGER NOT NULL, last_seen INTEGER NOT NULL, end INTEGER);
            CREATE TABLE IF NOT EXISTS observation_bees(
                run TEXT NOT NULL REFERENCES observation_runs(id) ON DELETE CASCADE, node TEXT NOT NULL,
                PRIMARY KEY(run,node));
            CREATE INDEX IF NOT EXISTS ix_observation_bees_node ON observation_bees(node,run);
            CREATE TABLE IF NOT EXISTS online_sessions(
                id TEXT PRIMARY KEY, node TEXT NOT NULL, run TEXT NOT NULL REFERENCES observation_runs(id) ON DELETE CASCADE,
                start INTEGER NOT NULL, last_seen INTEGER NOT NULL, end INTEGER);
            CREATE INDEX IF NOT EXISTS ix_online_sessions_node ON online_sessions(node,start);
            CREATE TABLE IF NOT EXISTS upgrade_windows(
                operation TEXT NOT NULL, node TEXT NOT NULL, session TEXT NOT NULL, source TEXT NOT NULL,
                start INTEGER NOT NULL, expires INTEGER NOT NULL, end INTEGER,
                PRIMARY KEY(operation,node));
            CREATE INDEX IF NOT EXISTS ix_upgrade_windows_node ON upgrade_windows(node,start);
            CREATE INDEX IF NOT EXISTS ix_upgrade_windows_active ON upgrade_windows(expires,node) WHERE end IS NULL;
            PRAGMA user_version=3;
            """;
        command.ExecuteNonQuery();
    }

    public long Register(HelloMessage hello, DateTimeOffset now)
    {
        if (!NodeMetadata.TryNormalize(hello.Node.Region, hello.Node.Ip, out var region, out var ip))
            throw new InvalidDataException("Invalid node region/IP: region must be at most 256 characters and IP must be a bare IPv4 or IPv6 address.");
        hello = hello with { Node = hello.Node with { Region = region, Ip = ip } };
        if (hello.Node.Id.Length is 0 or > 128 || hello.Node.Name.Length > 256 ||
            hello.Node.OperatingSystem.Length > 512 || !Guid.TryParseExact(hello.Node.DataEpoch, "N", out _) ||
            hello.LastAcknowledgedSequence < 0 || hello.LastSequence < hello.LastAcknowledgedSequence)
            throw new InvalidDataException("Invalid node identity/checkpoint.");
        var existing = Get(hello.Node.Id);
        if (existing is not null && (existing.Node.DataEpoch != hello.Node.DataEpoch ||
            existing.Sequence > hello.LastSequence || existing.Sequence < hello.LastAcknowledgedSequence))
            throw new InvalidDataException("Node/Hive database checkpoint mismatch; restore the original database or provision a new node ID.");
        if (existing is null && hello.LastAcknowledgedSequence > 0)
            throw new InvalidDataException("Hive database is missing previously acknowledged data. Restore the Hive database.");
        using var command = writer.CreateCommand();
        command.CommandText = """
            INSERT INTO nodes(id,node_json,epoch,received) VALUES($id,$json,$epoch,$now)
            ON CONFLICT(id) DO UPDATE SET node_json=excluded.node_json,received=excluded.received
            """;
        command.Parameters.AddWithValue("$id", hello.Node.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(hello.Node, ProtocolJson.Default.NodeInfo));
        command.Parameters.AddWithValue("$epoch", hello.Node.DataEpoch);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        return existing?.Sequence ?? 0;
    }

    public long Ingest(string id, BatchMessage batch, DateTimeOffset now)
    {
        if (batch.Records is not { Length: > 0 and <= 100 } || batch.DiscardedThroughSequence < 0)
            throw new InvalidDataException("Invalid batch size or discarded checkpoint.");
        var node = Get(id) ?? throw new InvalidDataException("Unknown node.");
        long sequence = node.Sequence;
        TrafficTotals? totals = node.Totals;
        NodeInfo info = node.Node;
        DateTimeOffset? sampled = node.LastSampledAt;
        using var transaction = writer.BeginTransaction();
        long previousInBatch = 0;
        foreach (var record in batch.Records)
        {
            Validate(record, now);
            if (record.Sequence <= previousInBatch) throw new InvalidDataException("Batch sequence must increase.");
            previousInBatch = record.Sequence;
            if (record.Sequence <= sequence) continue;
            bool missing = record.Sequence != sequence + 1;
            if (missing && batch.DiscardedThroughSequence != record.Sequence - 1)
                throw new InvalidDataException("Sequence gap without an explicit discarded-history checkpoint.");
            if (totals is not null && (record.Totals.StartedAt != totals.StartedAt ||
                record.Totals.ReceivedBytes - totals.ReceivedBytes < record.ReceivedBytes ||
                record.Totals.SentBytes - totals.SentBytes < record.SentBytes))
                throw new InvalidDataException("Cumulative counters cannot move backwards or contradict interval bytes.");
            bool clockSkew = record.BucketStart > now.AddMinutes(5) || record.SampledAt > now.AddMinutes(5);
            bool persistentGap = missing || clockSkew || (totals?.HasGaps ?? false) || record.Totals.HasGaps;
            DateTimeOffset? lastGap = record.Totals.LastGapAt;
            if (totals?.LastGapAt is { } priorGap && (lastGap is null || priorGap > lastGap)) lastGap = priorGap;
            if (missing && (lastGap is null || record.BucketStart > lastGap)) lastGap = record.BucketStart;
            if (clockSkew) lastGap = now;
            totals = record.Totals with { HasGaps = persistentGap, LastGapAt = lastGap };
            foreach (int step in new[] { 5, 60, 3600 })
            {
                // Still acknowledge durable cumulative counters so a clock correction cannot jam the outbox.
                if (clockSkew) continue;
                long bucket = HistoryQuery.Floor(record.BucketStart.ToUnixTimeSeconds(), step);
                if (bucket < HistoryQuery.Floor(HistoryQuery.Cutoff(step, now).ToUnixTimeSeconds(), step)) continue;
                using var aggregate = writer.CreateCommand();
                aggregate.CommandText = """
                    INSERT INTO buckets VALUES($node,$step,$start,$rx,$tx,$seconds,$peakRx,$peakTx,$gap,$interfaces)
                    ON CONFLICT(node,step,start) DO UPDATE SET
                        rx=rx+excluded.rx,tx=tx+excluded.tx,seconds=seconds+excluded.seconds,
                        peak_rx=MAX(peak_rx,excluded.peak_rx),peak_tx=MAX(peak_tx,excluded.peak_tx),
                        gap=MAX(gap,excluded.gap),interfaces=excluded.interfaces
                    """;
                aggregate.Parameters.AddWithValue("$node", id);
                aggregate.Parameters.AddWithValue("$step", step);
                aggregate.Parameters.AddWithValue("$start", bucket);
                aggregate.Parameters.AddWithValue("$rx", record.ReceivedBytes);
                aggregate.Parameters.AddWithValue("$tx", record.SentBytes);
                aggregate.Parameters.AddWithValue("$seconds", record.ObservedSeconds);
                aggregate.Parameters.AddWithValue("$peakRx", record.PeakDownloadBitsPerSecond);
                aggregate.Parameters.AddWithValue("$peakTx", record.PeakUploadBitsPerSecond);
                aggregate.Parameters.AddWithValue("$gap", record.HasGap || missing);
                aggregate.Parameters.AddWithValue("$interfaces", JsonSerializer.Serialize(record.Interfaces, ProtocolJson.Default.InterfaceInfoArray));
                aggregate.ExecuteNonQuery();
            }
            sequence = record.Sequence;
            sampled = record.SampledAt;
            info = info with { Interfaces = record.Interfaces };
        }
        using var update = writer.CreateCommand();
        update.CommandText = "UPDATE nodes SET seq=$seq,totals_json=$totals,node_json=$node,sampled=$sampled,received=$now WHERE id=$id";
        update.Parameters.AddWithValue("$seq", sequence);
        update.Parameters.AddWithValue("$totals", totals is null ? DBNull.Value : JsonSerializer.Serialize(totals, ProtocolJson.Default.TrafficTotals));
        update.Parameters.AddWithValue("$node", JsonSerializer.Serialize(info, ProtocolJson.Default.NodeInfo));
        update.Parameters.AddWithValue("$sampled", sampled is null ? DBNull.Value : sampled.Value.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        update.Parameters.AddWithValue("$id", id);
        update.ExecuteNonQuery();
        transaction.Commit();
        return sequence;
    }

    private static void Validate(TrafficRecord record, DateTimeOffset now)
    {
        if (record.Sequence <= 0 || record.ReceivedBytes < 0 || record.SentBytes < 0 ||
            record.Totals.ReceivedBytes < record.ReceivedBytes || record.Totals.SentBytes < record.SentBytes ||
            record.Totals.ReceivedBytes > long.MaxValue - record.Totals.SentBytes ||
            !double.IsFinite(record.ObservedSeconds) || record.ObservedSeconds < 0 || record.ObservedSeconds > 15 ||
            !double.IsFinite(record.PeakDownloadBitsPerSecond) || record.PeakDownloadBitsPerSecond < 0 ||
            !double.IsFinite(record.PeakUploadBitsPerSecond) || record.PeakUploadBitsPerSecond < 0 ||
            (record.ObservedSeconds == 0 && (record.ReceivedBytes != 0 || record.SentBytes != 0)) ||
            record.BucketStart.ToUnixTimeSeconds() % 5 != 0 || record.BucketStart.Millisecond != 0 ||
            record.Interfaces is not { Length: <= 64 } ||
            record.Interfaces.Any(x => x.Id.Length > 256 || x.Name.Length > 256 || x.LinkSpeedBitsPerSecond < 0))
            throw new InvalidDataException("Invalid traffic record.");
    }

    public StoredBee? Get(string id)
    {
        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT node_json,seq,totals_json,sampled,received FROM nodes WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBee(reader) : null;
    }

    public StoredBee[] List()
    {
        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT node_json,seq,totals_json,sampled,received FROM nodes ORDER BY id";
        using var reader = command.ExecuteReader();
        var result = new List<StoredBee>();
        while (reader.Read()) result.Add(ReadBee(reader));
        return result.ToArray();
    }

    private static StoredBee ReadBee(SqliteDataReader reader) => new(
        JsonSerializer.Deserialize(reader.GetString(0), ProtocolJson.Default.NodeInfo)!, reader.GetInt64(1),
        reader.IsDBNull(2) ? null : JsonSerializer.Deserialize(reader.GetString(2), ProtocolJson.Default.TrafficTotals),
        reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
        reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)));

    public HistoryResponse History(string id, HistoryQuery query)
    {
        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT start,rx,tx,seconds,peak_rx,peak_tx,gap,interfaces FROM buckets WHERE node=$id AND step=$step AND start >= $from AND start < $to ORDER BY start";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$step", query.StepSeconds);
        command.Parameters.AddWithValue("$from", query.FromSeconds);
        command.Parameters.AddWithValue("$to", query.ToSeconds);
        using var reader = command.ExecuteReader();
        var points = new Dictionary<long, HistoryPoint>();
        while (reader.Read())
        {
            long start = reader.GetInt64(0);
            double seconds = reader.GetDouble(3);
            bool available = seconds > 0;
            points.Add(start, new(DateTimeOffset.FromUnixTimeSeconds(start), DateTimeOffset.FromUnixTimeSeconds(start + query.StepSeconds),
                available ? reader.GetInt64(1) : null, available ? reader.GetInt64(2) : null,
                available ? reader.GetInt64(1) * 8.0 / seconds : null, available ? reader.GetInt64(2) * 8.0 / seconds : null,
                available ? reader.GetDouble(4) : null, available ? reader.GetDouble(5) : null,
                Math.Min(seconds, query.StepSeconds), reader.GetBoolean(6) || seconds < query.StepSeconds - 0.25,
                JsonSerializer.Deserialize(reader.GetString(7), ProtocolJson.Default.InterfaceInfoArray)!));
        }
        var result = new List<HistoryPoint>();
        for (long start = query.FromSeconds; start < query.ToSeconds; start += query.StepSeconds)
            result.Add(points.GetValueOrDefault(start) ?? new(DateTimeOffset.FromUnixTimeSeconds(start),
                DateTimeOffset.FromUnixTimeSeconds(start + query.StepSeconds), null, null, null, null, null, null, 0, true, []));
        return new(query.Step, DateTimeOffset.FromUnixTimeSeconds(query.FromSeconds), DateTimeOffset.FromUnixTimeSeconds(query.ToSeconds), result.ToArray());
    }

    public void Prune(DateTimeOffset now)
    {
        using var transaction = writer.BeginTransaction();
        foreach (int step in new[] { 5, 60, 3600 })
        {
            using var command = writer.CreateCommand();
            command.CommandText = "DELETE FROM buckets WHERE step=$step AND start<$cutoff";
            command.Parameters.AddWithValue("$step", step);
            command.Parameters.AddWithValue("$cutoff", HistoryQuery.Floor(HistoryQuery.Cutoff(step, now).ToUnixTimeSeconds(), step));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection OpenReader()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public void Dispose() => writer.Dispose();
}
