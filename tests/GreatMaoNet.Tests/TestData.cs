using GreatMaoNet.Contracts;
using GreatMaoNetBee;
using GreatMaoNetHive;
using Microsoft.Data.Sqlite;

namespace GreatMaoNet.Tests;

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GreatMaoNetTests", Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Path, recursive: true); }
}

public static class TestData
{
    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60);
    public static readonly InterfaceInfo Interface = new("eth0", "eth0", 1_000_000_000);
    public static CounterReading Counter(long received, long sent = 0, string id = "eth0") => new(Interface with { Id = id, Name = id }, id, received, sent);
    public static BeeCheckpoint Checkpoint() => new() { NodeId = "bee-01", Totals = new(Now.AddMinutes(-10), 0, 0, false, null) };
    public static NodeInfo Node(string id = "bee-01") => new(id, id, "test", Guid.NewGuid().ToString("N"), [Interface]);
    public static TrafficRecord Record(long sequence, DateTimeOffset? start = null, long received = 100, long sent = 50) =>
        new(sequence, start ?? Now.AddSeconds((sequence - 1) * 5), (start ?? Now.AddSeconds((sequence - 1) * 5)).AddSeconds(5), 5, received, sent, received * 8, sent * 8, false,
            new(Now.AddMinutes(-10), sequence * received, sequence * sent, false, null), [Interface]);
    public static HiveStore Store(TemporaryDirectory directory) => new(new HiveOptions { DatabasePath = directory.File("hive.db") });
}
