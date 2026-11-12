namespace GreatMaoNetHive;

public sealed partial class HiveStore
{
    private (string Operation, DateTimeOffset Expires)? pendingHiveUpgrade;

    public void BeginBeeUpgrade(string operation, string node, string session, DateTimeOffset now, int graceSeconds)
    {
        using var transaction = writer.BeginTransaction();
        using var check = writer.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM online_sessions WHERE id=$session AND node=$node AND run=$run AND end IS NULL AND last_seen+45000>$now";
        check.Parameters.AddWithValue("$session", session);
        check.Parameters.AddWithValue("$node", node);
        check.Parameters.AddWithValue("$run", observationRun);
        check.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        if ((long)check.ExecuteScalar()! != 1) throw new InvalidDataException("Upgrade requires an active session.");
        InsertUpgrade(operation, node, session, "bee", now, now.AddSeconds(graceSeconds));
        Observe(now);
        transaction.Commit();
    }

    public void BeginHiveUpgrade(string operation, DateTimeOffset now, int graceSeconds)
    {
        using var transaction = writer.BeginTransaction();
        using var command = writer.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO upgrade_windows(operation,node,session,source,start,expires)
            SELECT $operation,node,$run,'hive',$now,$expires FROM online_sessions
            WHERE run=$run AND end IS NULL AND last_seen+45000>$now;
            """;
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$run", observationRun);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", now.AddSeconds(graceSeconds).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        Observe(now);
        transaction.Commit();
        pendingHiveUpgrade = (operation, now.AddSeconds(graceSeconds));
    }

    public void CancelUpgrade(string operation, string session)
    {
        using var command = writer.CreateCommand();
        command.CommandText = "DELETE FROM upgrade_windows WHERE operation=$operation AND session=$session";
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$session", session);
        command.ExecuteNonQuery();
    }

    public void CancelHiveUpgrade(string operation)
    {
        CancelUpgrade(operation, observationRun);
        if (pendingHiveUpgrade?.Operation == operation) pendingHiveUpgrade = null;
    }

    public HashSet<string> UpgradingNodes(DateTimeOffset now)
    {
        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT node FROM upgrade_windows WHERE start<=$now AND expires>$now AND end IS NULL";
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private void InsertUpgrade(string operation, string node, string session, string source, DateTimeOffset now, DateTimeOffset expires)
    {
        using var command = writer.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO upgrade_windows(operation,node,session,source,start,expires) VALUES($operation,$node,$session,$source,$now,$expires)";
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$node", node);
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private void FinishUpgradesOnReconnect(string node, DateTimeOffset now)
    {
        using var command = writer.CreateCommand();
        command.CommandText = "UPDATE upgrade_windows SET end=MAX(start,MIN(expires,$now)) WHERE node=$node AND end IS NULL AND ($pending IS NULL OR operation<>$pending)";
        command.Parameters.AddWithValue("$node", node);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$pending", (object?)pendingHiveUpgrade?.Operation ?? DBNull.Value);
        command.ExecuteNonQuery();
        // A connection admitted while Hive is preparing to stop must also be protected.
        if (pendingHiveUpgrade is { } upgrade && now < upgrade.Expires)
            InsertUpgrade(upgrade.Operation, node, observationRun, "hive", now, upgrade.Expires);
    }
}
