using GreatMaoNet.Contracts;

namespace GreatMaoNetHive;

public sealed partial class HiveStore
{
    private readonly string observationRun = Guid.NewGuid().ToString("N");

    public void StartObservation(IEnumerable<string> nodeIds, DateTimeOffset now)
    {
        using var transaction = writer.BeginTransaction();
        using var command = writer.CreateCommand();
        command.CommandText = """
            UPDATE observation_runs SET end=last_seen WHERE end IS NULL;
            UPDATE online_sessions SET end=MAX(start,MIN(last_seen+45000,
                (SELECT end FROM observation_runs WHERE id=online_sessions.run))) WHERE end IS NULL;
            INSERT INTO observation_runs(id,start,last_seen) VALUES($run,$now,$now);
            """;
        command.Parameters.AddWithValue("$run", observationRun);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        foreach (string id in nodeIds)
        {
            using var node = writer.CreateCommand();
            node.CommandText = "INSERT INTO observation_bees VALUES($run,$node)";
            node.Parameters.AddWithValue("$run", observationRun);
            node.Parameters.AddWithValue("$node", id);
            node.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Observe(DateTimeOffset now)
    {
        using var command = writer.CreateCommand();
        command.CommandText = "UPDATE observation_runs SET last_seen=MAX(last_seen,$now) WHERE id=$run AND end IS NULL";
        command.Parameters.AddWithValue("$run", observationRun);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public void OpenOnlineSession(string sessionId, string nodeId, DateTimeOffset now)
    {
        using var transaction = writer.BeginTransaction();
        Observe(now);
        FinishUpgradesOnReconnect(nodeId, now);
        using var command = writer.CreateCommand();
        command.CommandText = """
            INSERT INTO online_sessions(id,node,run,start,last_seen)
            SELECT $session,$node,id,$now,$now FROM observation_runs WHERE id=$run AND end IS NULL;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$run", observationRun);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void TouchOnlineSession(string sessionId, DateTimeOffset now)
    {
        using var transaction = writer.BeginTransaction();
        Observe(now);
        using var command = writer.CreateCommand();
        command.CommandText = "UPDATE online_sessions SET last_seen=MAX(last_seen,$now) WHERE id=$session AND run=$run AND end IS NULL";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$run", observationRun);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void CloseOnlineSession(string sessionId, DateTimeOffset now)
    {
        using var transaction = writer.BeginTransaction();
        Observe(now);
        using var command = writer.CreateCommand();
        command.CommandText = "UPDATE online_sessions SET end=MAX(start,MIN($now,last_seen+45000)) WHERE id=$session AND run=$run AND end IS NULL";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$run", observationRun);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void StopObservation(DateTimeOffset now)
    {
        using var transaction = writer.BeginTransaction();
        Observe(now);
        using var command = writer.CreateCommand();
        command.CommandText = """
            UPDATE observation_runs SET end=last_seen WHERE id=$run AND end IS NULL;
            UPDATE online_sessions SET end=MAX(start,MIN($now,last_seen+45000)) WHERE run=$run AND end IS NULL;
            """;
        command.Parameters.AddWithValue("$run", observationRun);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void PruneOnlineHistory(DateTimeOffset cutoff)
    {
        using var transaction = writer.BeginTransaction();
        using var command = writer.CreateCommand();
        // Trim observation coverage too. Increasing retention later must not turn deleted
        // online intervals into apparently observed offline time in a long-running Hive.
        command.CommandText = """
            DELETE FROM online_sessions WHERE end <= $cutoff;
            DELETE FROM upgrade_windows WHERE COALESCE(end,expires) <= $cutoff;
            DELETE FROM observation_runs WHERE end <= $cutoff;
            UPDATE observation_runs SET start=$cutoff WHERE start<$cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public OnlineHistoryResponse OnlineHistory(string id, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, int retentionDays)
    {
        if (from >= to || to > now || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("from must precede to; to cannot be in the future; maximum window is 31 days.");
        long left = from.ToUnixTimeMilliseconds(), right = to.ToUnixTimeMilliseconds();
        if (left >= right) throw new ArgumentException("Online history requires a window of at least one millisecond.");
        long retained = Math.Max(left, now.AddDays(-retentionDays).ToUnixTimeMilliseconds());
        var events = new SortedDictionary<long, (int Observed, int Online, int Upgrading)> { [left] = (0, 0, 0), [right] = (0, 0, 0) };
        using var connection = OpenReader();
        using var transaction = connection.BeginTransaction(deferred: true);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT r.start,COALESCE(r.end,r.last_seen) FROM observation_runs r
                JOIN observation_bees b ON b.run=r.id WHERE b.node=$id AND r.start<$to AND COALESCE(r.end,r.last_seen)>$from
                LIMIT 10001
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$from", retained);
            command.Parameters.AddWithValue("$to", right);
            using var reader = command.ExecuteReader();
            int count = 0;
            while (reader.Read())
            {
                if (++count > 10000) throw new ArgumentException("Too many observation intervals; shorten the time window.");
                Add(reader.GetInt64(0), reader.GetInt64(1), online: false);
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT MAX(s.start,r.start),MIN(COALESCE(s.end,s.last_seen+45000),COALESCE(r.end,r.last_seen))
                FROM online_sessions s JOIN observation_runs r ON r.id=s.run
                WHERE s.node=$id AND s.start<$to AND COALESCE(s.end,s.last_seen+45000)>$from
                LIMIT 10001
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$from", retained);
            command.Parameters.AddWithValue("$to", right);
            using var reader = command.ExecuteReader();
            int count = 0;
            while (reader.Read())
            {
                if (++count > 10000) throw new ArgumentException("Too many online intervals; shorten the time window.");
                Add(reader.GetInt64(0), reader.GetInt64(1), online: true);
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT start,COALESCE(end,expires) FROM upgrade_windows WHERE node=$id AND start<$to AND COALESCE(end,expires)>$from LIMIT 10001";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$from", retained);
            command.Parameters.AddWithValue("$to", right);
            using var reader = command.ExecuteReader();
            int count = 0;
            while (reader.Read())
            {
                if (++count > 10000) throw new ArgumentException("Too many upgrade intervals; shorten the time window.");
                Add(reader.GetInt64(0), reader.GetInt64(1), online: false, upgrading: true);
            }
        }
        transaction.Commit();
        var intervals = new List<OnlineInterval>();
        long previous = left;
        int observedCount = 0, onlineCount = 0, upgradingCount = 0;
        double onlineSeconds = 0, offlineSeconds = 0, unknownSeconds = 0, upgradingSeconds = 0;
        foreach (var (time, change) in events)
        {
            if (time > previous)
            {
                string status = upgradingCount > 0 ? "upgrading" : observedCount == 0 ? "unknown" : onlineCount > 0 ? "online" : "offline";
                double seconds = (time - previous) / 1000.0;
                if (status == "online") onlineSeconds += seconds;
                else if (status == "offline") offlineSeconds += seconds;
                else if (status == "upgrading") upgradingSeconds += seconds;
                else unknownSeconds += seconds;
                var end = DateTimeOffset.FromUnixTimeMilliseconds(time);
                if (intervals.Count > 0 && intervals[^1].Status == status) intervals[^1] = intervals[^1] with { To = end };
                else intervals.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(previous), end, status));
            }
            observedCount += change.Observed;
            onlineCount += change.Online;
            upgradingCount += change.Upgrading;
            previous = time;
        }
        return new(id, DateTimeOffset.FromUnixTimeMilliseconds(left), DateTimeOffset.FromUnixTimeMilliseconds(right), now.ToUniversalTime(),
            onlineSeconds, offlineSeconds, unknownSeconds, intervals.ToArray()) { UpgradingSeconds = upgradingSeconds };

        void Add(long start, long end, bool online, bool upgrading = false)
        {
            start = Math.Max(start, retained);
            end = Math.Min(end, right);
            if (start >= end) return;
            var a = events.GetValueOrDefault(start);
            var b = events.GetValueOrDefault(end);
            events[start] = (a.Observed + (online || upgrading ? 0 : 1), a.Online + (online ? 1 : 0), a.Upgrading + (upgrading ? 1 : 0));
            events[end] = (b.Observed - (online || upgrading ? 0 : 1), b.Online - (online ? 1 : 0), b.Upgrading - (upgrading ? 1 : 0));
        }
    }
}
