using GreatMaoNetHive;
using Microsoft.Data.Sqlite;

namespace GreatMaoNet.Tests;

public sealed class OnlineHistoryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T00:00:00Z");

    [Fact]
    public void RecordsConnectionsReconnectionsAndClipsWindowWithoutOverlaps()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        store.StartObservation(["bee-01", "unseen"], Start);
        store.OpenOnlineSession("first", "bee-01", Start.AddSeconds(10));
        store.TouchOnlineSession("first", Start.AddSeconds(20));
        store.CloseOnlineSession("first", Start.AddSeconds(30));
        store.OpenOnlineSession("second", "bee-01", Start.AddSeconds(40));
        store.CloseOnlineSession("first", Start.AddSeconds(45)); // Delayed cleanup must not affect the new connection.
        store.TouchOnlineSession("second", Start.AddSeconds(50));
        store.Observe(Start.AddSeconds(60));
        var result = store.OnlineHistory("bee-01", Start.AddSeconds(-10), Start.AddSeconds(70), Start.AddSeconds(70), 90);
        Assert.Equal(40, result.OnlineSeconds);
        Assert.Equal(20, result.OfflineSeconds);
        Assert.Equal(20, result.UnknownSeconds);
        Assert.Equal(new[] { "unknown", "offline", "online", "offline", "online", "unknown" }, result.Intervals.Select(x => x.Status));
        for (int i = 1; i < result.Intervals.Length; i++) Assert.Equal(result.Intervals[i - 1].To, result.Intervals[i].From);
        var clipped = store.OnlineHistory("bee-01", Start.AddSeconds(15), Start.AddSeconds(25), Start.AddSeconds(70), 90);
        Assert.Equal(10, clipped.OnlineSeconds);
        Assert.Single(clipped.Intervals);
        Assert.Equal(60, store.OnlineHistory("unseen", Start, Start.AddSeconds(60), Start.AddSeconds(70), 90).OfflineSeconds);
        Assert.Equal(60, store.OnlineHistory("unconfigured", Start, Start.AddSeconds(60), Start.AddSeconds(70), 90).UnknownSeconds);
    }

    [Fact]
    public void CrashRecoveryDoesNotClaimHiveDowntimeWasBeeOffline()
    {
        using var directory = new TemporaryDirectory();
        using (var store = TestData.Store(directory))
        {
            store.StartObservation(["bee-01"], Start);
            store.OpenOnlineSession("old", "bee-01", Start.AddSeconds(5));
            store.TouchOnlineSession("old", Start.AddSeconds(15));
            store.Observe(Start.AddSeconds(20));
        } // No StopObservation: simulate abrupt process death.
        using (var store = TestData.Store(directory))
        {
            store.StartObservation(["bee-01"], Start.AddSeconds(40));
            store.OpenOnlineSession("new", "bee-01", Start.AddSeconds(50));
            store.StopObservation(Start.AddSeconds(60));
            var result = store.OnlineHistory("bee-01", Start, Start.AddSeconds(70), Start.AddSeconds(70), 90);
            Assert.Equal(25, result.OnlineSeconds);
            Assert.Equal(15, result.OfflineSeconds);
            Assert.Equal(30, result.UnknownSeconds);
        }
    }

    [Fact]
    public void SilentSessionsExpireAndRetentionNeverTurnsUnknownIntoOffline()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        store.StartObservation(["bee-01"], Start);
        store.OpenOnlineSession("silent", "bee-01", Start);
        store.Observe(Start.AddSeconds(90));
        var result = store.OnlineHistory("bee-01", Start, Start.AddSeconds(90), Start.AddSeconds(90), 90);
        Assert.Equal(45, result.OnlineSeconds);
        Assert.Equal(45, result.OfflineSeconds);
        store.StopObservation(Start.AddSeconds(90));
        store.PruneOnlineHistory(Start.AddDays(2));
        result = store.OnlineHistory("bee-01", Start, Start.AddSeconds(90), Start.AddDays(2), 1);
        Assert.Equal(90, result.UnknownSeconds);
        Assert.Equal(0, result.OfflineSeconds);
    }

    [Fact]
    public void InvalidWindowsAreRejected()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        Assert.Throws<ArgumentException>(() => store.OnlineHistory("bee-01", Start, Start, Start, 90));
        Assert.Throws<ArgumentException>(() => store.OnlineHistory("bee-01", Start, Start.AddSeconds(1), Start, 90));
        Assert.Throws<ArgumentException>(() => store.OnlineHistory("bee-01", Start, Start.AddDays(32), Start.AddDays(33), 90));
    }

    [Fact]
    public void IncreasingRetentionCannotMislabelDeletedOnlineRecordsAsOffline()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        store.StartObservation(["bee-01"], Start);
        store.OpenOnlineSession("expired", "bee-01", Start);
        store.CloseOnlineSession("expired", Start.AddSeconds(30));
        store.Observe(Start.AddDays(2));
        store.PruneOnlineHistory(Start.AddDays(1));
        var result = store.OnlineHistory("bee-01", Start, Start.AddSeconds(60), Start.AddDays(2), 90);
        Assert.Equal(60, result.UnknownSeconds);
        Assert.Equal(0, result.OfflineSeconds);
        result = store.OnlineHistory("bee-01", Start.AddDays(1), Start.AddDays(1).AddSeconds(60), Start.AddDays(2), 90);
        Assert.Equal(60, result.OfflineSeconds);
    }

    [Fact]
    public void ExistingVersionOneDatabaseIsUpgradedWithoutLosingNodes()
    {
        using var directory = new TemporaryDirectory();
        var existingNode = TestData.Node();
        using (var original = TestData.Store(directory)) original.Register(new(existingNode, 0, 0), Start);
        using (var connection = new SqliteConnection($"Data Source={directory.File("hive.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE upgrade_windows; DROP TABLE online_sessions; DROP TABLE observation_bees; DROP TABLE observation_runs; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        using (var store = TestData.Store(directory))
        {
            Assert.Equal(existingNode.DataEpoch, store.Get(existingNode.Id)!.Node.DataEpoch);
            store.StartObservation(["bee-01"], Start);
            store.StopObservation(Start.AddSeconds(5));
            Assert.Equal(5, store.OnlineHistory("bee-01", Start, Start.AddSeconds(5), Start.AddSeconds(5), 90).OfflineSeconds);
        }
        using var reopened = new SqliteConnection($"Data Source={directory.File("hive.db")}");
        reopened.Open();
        using var check = reopened.CreateCommand();
        check.CommandText = "PRAGMA user_version";
        Assert.Equal(3L, check.ExecuteScalar());
    }
}
