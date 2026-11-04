using GreatMaoNet.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GreatMaoNetBee;

public sealed class BeeStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection connection;

    public BeeStore(BeeOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath }.ToString());
        connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; " +
            "CREATE TABLE IF NOT EXISTS checkpoint(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL); " +
            "CREATE TABLE IF NOT EXISTS delivery(id INTEGER PRIMARY KEY CHECK(id=1), ack INTEGER NOT NULL, discarded INTEGER NOT NULL); " +
            "INSERT OR IGNORE INTO delivery VALUES(1,0,0); " +
            "CREATE TABLE IF NOT EXISTS outbox(seq INTEGER PRIMARY KEY, created INTEGER NOT NULL, json TEXT NOT NULL);");
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT json FROM checkpoint WHERE id=1";
        if (query.ExecuteScalar() is not string)
        {
            var initial = new BeeCheckpoint { NodeId = options.Id, Totals = new(DateTimeOffset.UtcNow, 0, 0, false, null) };
            Execute("INSERT INTO checkpoint VALUES(1,$json)", ("$json", JsonSerializer.Serialize(initial, BeeJson.Default.BeeCheckpoint)));
        }
        if (Load().NodeId != options.Id) throw new InvalidOperationException("Database belongs to another Bee:Id. Use its original ID or a new database.");
    }

    public BeeCheckpoint Load()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM checkpoint WHERE id=1";
            return JsonSerializer.Deserialize((string)command.ExecuteScalar()!, BeeJson.Default.BeeCheckpoint)!;
        }
    }

    public void Commit(BeeCheckpoint checkpoint, TrafficRecord record, DateTimeOffset now)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            Execute("UPDATE checkpoint SET json=$json WHERE id=1", ("$json", JsonSerializer.Serialize(checkpoint, BeeJson.Default.BeeCheckpoint)));
            Execute("INSERT INTO outbox VALUES($seq,$created,$json)", ("$seq", record.Sequence),
                ("$created", now.ToUnixTimeSeconds()), ("$json", JsonSerializer.Serialize(record, ProtocolJson.Default.TrafficRecord)));
            Execute("UPDATE delivery SET discarded=MAX(discarded,COALESCE((SELECT MAX(seq) FROM outbox WHERE created<$cutoff),0)) WHERE id=1",
                ("$cutoff", now.AddDays(-7).ToUnixTimeSeconds()));
            Execute("DELETE FROM outbox WHERE seq<=(SELECT discarded FROM delivery WHERE id=1)");
            transaction.Commit();
        }
    }

    public (long Ack, long Discarded) Delivery()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ack,discarded FROM delivery WHERE id=1";
            using var reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetInt64(0), reader.GetInt64(1));
        }
    }

    public BatchMessage Pending()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM outbox ORDER BY seq LIMIT 100";
            using var reader = command.ExecuteReader();
            var records = new List<TrafficRecord>();
            while (reader.Read()) records.Add(JsonSerializer.Deserialize(reader.GetString(0), ProtocolJson.Default.TrafficRecord)!);
            reader.Close();
            return new BatchMessage(records.ToArray(), Delivery().Discarded);
        }
    }

    public void Acknowledge(long sequence)
    {
        lock (gate)
        {
            if (sequence < 0 || sequence > Load().Sequence) throw new InvalidDataException("Invalid acknowledgement.");
            using var transaction = connection.BeginTransaction();
            Execute("UPDATE delivery SET ack=MAX(ack,$seq) WHERE id=1", ("$seq", sequence));
            Execute("DELETE FROM outbox WHERE seq<=$seq", ("$seq", sequence));
            transaction.Commit();
        }
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    public void Dispose() => connection.Dispose();
}
