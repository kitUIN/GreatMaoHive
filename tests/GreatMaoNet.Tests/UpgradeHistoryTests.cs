using GreatMaoNetHive;

namespace GreatMaoNet.Tests;

public sealed class UpgradeHistoryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T00:00:00Z");

    [Fact]
    public void BeeUpgradeSurvivesHiveRestartAndEndsAtReconnectWithoutOfflineTime()
    {
        using var directory = new TemporaryDirectory();
        using (var store = TestData.Store(directory))
        {
            store.StartObservation(["bee-01"], Start);
            store.OpenOnlineSession("old", "bee-01", Start);
            store.BeginBeeUpgrade("update", "bee-01", "old", Start.AddSeconds(10), 300);
            store.CloseOnlineSession("old", Start.AddSeconds(11));
            store.StopObservation(Start.AddSeconds(20));
        }
        using (var store = TestData.Store(directory))
        {
            store.StartObservation(["bee-01"], Start.AddSeconds(30));
            Assert.Contains("bee-01", store.UpgradingNodes(Start.AddSeconds(35)));
            store.OpenOnlineSession("new", "bee-01", Start.AddSeconds(40));
            store.CloseOnlineSession("old", Start.AddSeconds(45));
            store.Observe(Start.AddSeconds(50));
            Assert.Empty(store.UpgradingNodes(Start.AddSeconds(50)));
            var history = store.OnlineHistory("bee-01", Start, Start.AddSeconds(50), Start.AddSeconds(50), 90);
            Assert.Equal(20, history.OnlineSeconds);
            Assert.Equal(30, history.UpgradingSeconds);
            Assert.Equal(0, history.OfflineSeconds);
            Assert.Equal(0, history.UnknownSeconds);
            Assert.Equal(new[] { "online", "upgrading", "online" }, history.Intervals.Select(x => x.Status));
            var clipped = store.OnlineHistory("bee-01", Start.AddSeconds(15), Start.AddSeconds(25), Start.AddSeconds(50), 90);
            Assert.Equal(10, clipped.UpgradingSeconds);
        }
    }

    [Fact]
    public void HiveUpgradeCoversShutdownAndReconnectButDoesNotMaskAlreadyOfflineNodes()
    {
        using var directory = new TemporaryDirectory();
        using (var store = TestData.Store(directory))
        {
            store.StartObservation(["bee-01", "offline", "late"], Start);
            store.OpenOnlineSession("old", "bee-01", Start);
            store.BeginHiveUpgrade("hive-update", Start.AddSeconds(10), 300);
            store.OpenOnlineSession("late-session", "late", Start.AddSeconds(11));
            store.CloseOnlineSession("old", Start.AddSeconds(12));
            store.StopObservation(Start.AddSeconds(15));
        }
        using var resumed = TestData.Store(directory);
        resumed.StartObservation(["bee-01", "offline", "late"], Start.AddSeconds(30));
        Assert.Contains("bee-01", resumed.UpgradingNodes(Start.AddSeconds(35)));
        Assert.Contains("late", resumed.UpgradingNodes(Start.AddSeconds(35)));
        Assert.DoesNotContain("offline", resumed.UpgradingNodes(Start.AddSeconds(35)));
        resumed.OpenOnlineSession("new", "bee-01", Start.AddSeconds(40));
        resumed.Observe(Start.AddSeconds(50));
        var history = resumed.OnlineHistory("bee-01", Start, Start.AddSeconds(50), Start.AddSeconds(50), 90);
        Assert.Equal(30, history.UpgradingSeconds);
        Assert.Equal(20, history.OnlineSeconds);
        Assert.Equal(0, history.OfflineSeconds);
        Assert.Equal(0, history.UnknownSeconds);
        var offline = resumed.OnlineHistory("offline", Start, Start.AddSeconds(50), Start.AddSeconds(50), 90);
        Assert.Equal(35, offline.OfflineSeconds);
        Assert.Equal(15, offline.UnknownSeconds);
        Assert.Equal(0, offline.UpgradingSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedUpgradeExpiresAndDoesNotHideLaterOutage(bool hive)
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        store.StartObservation(["bee-01"], Start);
        store.OpenOnlineSession("session", "bee-01", Start);
        if (hive) store.BeginHiveUpgrade("update", Start.AddSeconds(10), 30);
        else store.BeginBeeUpgrade("update", "bee-01", "session", Start.AddSeconds(10), 30);
        store.CloseOnlineSession("session", Start.AddSeconds(11));
        store.Observe(Start.AddSeconds(60));
        Assert.Contains("bee-01", store.UpgradingNodes(Start.AddSeconds(39)));
        Assert.Empty(store.UpgradingNodes(Start.AddSeconds(40)));
        var history = store.OnlineHistory("bee-01", Start, Start.AddSeconds(60), Start.AddSeconds(60), 90);
        Assert.Equal(10, history.OnlineSeconds);
        Assert.Equal(30, history.UpgradingSeconds);
        Assert.Equal(20, history.OfflineSeconds);
        store.OpenOnlineSession("recovered", "bee-01", Start.AddSeconds(65));
        Assert.Equal(30, store.OnlineHistory("bee-01", Start, Start.AddSeconds(65), Start.AddSeconds(65), 90).UpgradingSeconds);
    }

    [Fact]
    public void CancelledPreparationRestoresNormalHistoryAndAllowsAnotherAttempt()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        store.StartObservation(["bee-01"], Start);
        store.OpenOnlineSession("session", "bee-01", Start);
        store.BeginBeeUpgrade("first", "bee-01", "session", Start.AddSeconds(10), 300);
        store.CancelUpgrade("first", "different-session");
        Assert.Contains("bee-01", store.UpgradingNodes(Start.AddSeconds(11)));
        store.CancelUpgrade("first", "session");
        store.BeginHiveUpgrade("hive", Start.AddSeconds(12), 300);
        store.CancelHiveUpgrade("hive");
        Assert.Empty(store.UpgradingNodes(Start.AddSeconds(13)));
        store.BeginBeeUpgrade("second", "bee-01", "session", Start.AddSeconds(20), 300);
        store.CancelUpgrade("second", "session");
        store.CloseOnlineSession("session", Start.AddSeconds(30));
        store.Observe(Start.AddSeconds(40));
        var history = store.OnlineHistory("bee-01", Start, Start.AddSeconds(40), Start.AddSeconds(40), 90);
        Assert.Equal(30, history.OnlineSeconds);
        Assert.Equal(10, history.OfflineSeconds);
        Assert.Equal(0, history.UpgradingSeconds);
    }

    [Fact]
    public void OverlappingUpgradesCountOnceAndRetentionDoesNotInventCoverage()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        store.StartObservation(["bee-01"], Start);
        store.OpenOnlineSession("session", "bee-01", Start);
        store.BeginBeeUpgrade("bee", "bee-01", "session", Start.AddSeconds(10), 30);
        store.BeginHiveUpgrade("hive", Start.AddSeconds(20), 30);
        store.CloseOnlineSession("session", Start.AddSeconds(21));
        store.StopObservation(Start.AddSeconds(30));
        var history = store.OnlineHistory("bee-01", Start, Start.AddSeconds(60), Start.AddSeconds(60), 90);
        Assert.Equal(40, history.UpgradingSeconds);
        Assert.Equal(10, history.OnlineSeconds);
        Assert.Equal(10, history.UnknownSeconds);
        store.PruneOnlineHistory(Start.AddDays(1));
        history = store.OnlineHistory("bee-01", Start, Start.AddSeconds(60), Start.AddDays(2), 90);
        Assert.Equal(60, history.UnknownSeconds);
        Assert.Equal(0, history.UpgradingSeconds);
        Assert.Equal(0, history.OfflineSeconds);
    }

    [Fact]
    public void StaleOrForeignSessionCannotRegisterBeeUpgradeAndRetryDoesNotExtendDeadline()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        store.StartObservation(["bee-01"], Start);
        store.OpenOnlineSession("session", "bee-01", Start);
        Assert.Throws<InvalidDataException>(() => store.BeginBeeUpgrade("bad", "other", "session", Start, 300));
        store.BeginBeeUpgrade("same", "bee-01", "session", Start.AddSeconds(5), 30);
        store.BeginBeeUpgrade("same", "bee-01", "session", Start.AddSeconds(20), 30);
        Assert.Empty(store.UpgradingNodes(Start.AddSeconds(35)));
        Assert.Throws<InvalidDataException>(() => store.BeginBeeUpgrade("stale", "bee-01", "session", Start.AddSeconds(46), 300));
        store.BeginHiveUpgrade("hive", Start.AddSeconds(46), 300);
        Assert.Empty(store.UpgradingNodes(Start.AddSeconds(47)));
    }
}
