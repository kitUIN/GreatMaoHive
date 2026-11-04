using GreatMaoNet.Contracts;
using GreatMaoNetBee;
using GreatMaoNetHive;

namespace GreatMaoNet.Tests;

public sealed class StorageTests
{
    [Fact]
    public void BeeCheckpointAndOutboxSurviveReopenAndAcknowledgement()
    {
        using var directory = new TemporaryDirectory();
        var options = new BeeOptions { Id = "bee-01", DatabasePath = directory.File("bee.db") };
        string epoch;
        using (var store = new BeeStore(options))
        {
            var state = store.Load();
            epoch = state.DataEpoch;
            state.Sequence = 1;
            state.Totals = TestData.Record(1).Totals;
            store.Commit(state, TestData.Record(1), TestData.Now);
        }
        using (var store = new BeeStore(options))
        {
            Assert.Equal(epoch, store.Load().DataEpoch);
            Assert.Equal(150, store.Load().Totals.TotalBytes);
            Assert.Single(store.Pending().Records);
            store.Acknowledge(1);
            store.Acknowledge(1);
            Assert.Empty(store.Pending().Records);
            Assert.Equal(1, store.Delivery().Ack);
            Assert.Throws<InvalidDataException>(() => store.Acknowledge(2));
        }
    }

    [Fact]
    public void ExpiringOutboxPreservesTotalsAndReportsDiscardedSequence()
    {
        using var directory = new TemporaryDirectory();
        using var store = new BeeStore(new BeeOptions { Id = "bee-01", DatabasePath = directory.File("bee.db") });
        var state = store.Load();
        state.Sequence = 1;
        store.Commit(state, TestData.Record(1), TestData.Now.AddDays(-8));
        state.Sequence = 2;
        state.Totals = TestData.Record(2).Totals;
        store.Commit(state, TestData.Record(2), TestData.Now);
        Assert.Equal(1, store.Pending().DiscardedThroughSequence);
        Assert.Equal(2, Assert.Single(store.Pending().Records).Sequence);
        Assert.Equal(300, store.Load().Totals.TotalBytes);
    }

    [Fact]
    public void DuplicateBatchAndReconnectNeverDoubleCount()
    {
        using var directory = new TemporaryDirectory();
        var node = TestData.Node();
        using (var store = TestData.Store(directory))
        {
            store.Register(new(node, 0, 0), TestData.Now);
            var batch = new BatchMessage([TestData.Record(1), TestData.Record(2)], 0);
            Assert.Equal(2, store.Ingest(node.Id, batch, TestData.Now.AddMinutes(1)));
            Assert.Equal(2, store.Ingest(node.Id, batch, TestData.Now.AddMinutes(1)));
            Assert.Equal(300, store.Get(node.Id)!.Totals!.TotalBytes);
            var history = store.History(node.Id, new(60, TestData.Now.ToUnixTimeSeconds(), TestData.Now.AddMinutes(1).ToUnixTimeSeconds()));
            Assert.Equal(200, Assert.Single(history.Points).ReceivedBytes);
            Assert.Equal(800, history.Points[0].PeakDownloadBitsPerSecond);
        }
        using (var store = TestData.Store(directory)) Assert.Equal(2, store.Register(new(node, 1, 2), TestData.Now));
    }

    [Fact]
    public void InvalidBatchRollsBackAllRowsAndCounterCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var node = TestData.Node();
        store.Register(new(node, 0, 0), TestData.Now);
        Assert.Throws<InvalidDataException>(() => store.Ingest(node.Id, new([TestData.Record(1), TestData.Record(3)], 0), TestData.Now));
        Assert.Equal(0, store.Get(node.Id)!.Sequence);
        Assert.Null(Assert.Single(store.History(node.Id, new(60, TestData.Now.ToUnixTimeSeconds(), TestData.Now.AddMinutes(1).ToUnixTimeSeconds())).Points).ReceivedBytes);
    }

    [Fact]
    public void DiscardedHistoryAdvancesLifetimeTotalsAndLeavesNullBuckets()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var node = TestData.Node();
        store.Register(new(node, 0, 0), TestData.Now);
        store.Ingest(node.Id, new([TestData.Record(3)], 2), TestData.Now);
        var total = store.Get(node.Id)!.Totals!;
        Assert.Equal(450, total.TotalBytes);
        Assert.True(total.HasGaps);
        var history = store.History(node.Id, new(5, TestData.Now.ToUnixTimeSeconds(), TestData.Now.AddSeconds(15).ToUnixTimeSeconds()));
        Assert.Null(history.Points[0].DownloadBitsPerSecond);
        Assert.True(history.Points[2].HasGap);
        store.Ingest(node.Id, new([TestData.Record(4)], 2), TestData.Now);
        Assert.True(store.Get(node.Id)!.Totals!.HasGaps);
    }

    [Fact]
    public void DelayedReplayPopulatesRetainedRollupsWithoutResurrectingExpiredRawData()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var node = TestData.Node();
        store.Register(new(node, 0, 0), TestData.Now);
        var start = TestData.Now.AddDays(-2);
        store.Ingest(node.Id, new([TestData.Record(1, start)], 0), TestData.Now);
        Assert.Null(Assert.Single(store.History(node.Id, new(5, start.ToUnixTimeSeconds(), start.AddSeconds(5).ToUnixTimeSeconds())).Points).ReceivedBytes);
        Assert.Equal(100, Assert.Single(store.History(node.Id, new(60, start.ToUnixTimeSeconds(), start.AddMinutes(1).ToUnixTimeSeconds())).Points).ReceivedBytes);
        store.Prune(TestData.Now.AddDays(91));
        Assert.Equal(100, Assert.Single(store.History(node.Id, new(3600, start.ToUnixTimeSeconds() / 3600 * 3600, start.ToUnixTimeSeconds() / 3600 * 3600 + 3600)).Points).ReceivedBytes);
        Assert.Equal(150, store.Get(node.Id)!.Totals!.TotalBytes);
    }

    [Fact]
    public void EpochResetAndLostHiveDatabaseFailExplicitly()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var node = TestData.Node();
        store.Register(new(node, 0, 0), TestData.Now);
        Assert.Throws<InvalidDataException>(() => store.Register(new(TestData.Node(), 0, 0), TestData.Now));
        Assert.Throws<InvalidDataException>(() => store.Register(new(TestData.Node("missing"), 1, 2), TestData.Now));
    }

    [Fact]
    public void FutureClockRecordsPreserveTotalsWithoutBlockingCorrectedSamples()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var node = TestData.Node();
        store.Register(new(node, 0, 0), TestData.Now);
        Assert.Equal(1, store.Ingest(node.Id, new([TestData.Record(1, TestData.Now.AddHours(1))], 0), TestData.Now));
        Assert.True(store.Get(node.Id)!.Totals!.HasGaps);
        Assert.Equal(2, store.Ingest(node.Id, new([TestData.Record(2)], 0), TestData.Now));
        Assert.Equal(300, store.Get(node.Id)!.Totals!.TotalBytes);
        var history = store.History(node.Id, new(60, TestData.Now.ToUnixTimeSeconds(), TestData.Now.AddMinutes(1).ToUnixTimeSeconds()));
        Assert.Equal(100, Assert.Single(history.Points).ReceivedBytes);
    }

    [Theory]
    [InlineData(1, "5s")]
    [InlineData(48, "1m")]
    [InlineData(2400, "1h")]
    public void AutoResolutionHonorsRetention(int hours, string expected) =>
        Assert.Equal(expected, HistoryQuery.Create(TestData.Now.AddHours(-hours), TestData.Now, "auto", TestData.Now).Step);

    [Fact]
    public void HistoryRejectsExpiredResolutionTooManyPointsAndInvalidRange()
    {
        Assert.Throws<ArgumentException>(() => HistoryQuery.Create(TestData.Now.AddDays(-2), TestData.Now, "5s", TestData.Now));
        Assert.Throws<ArgumentException>(() => HistoryQuery.Create(TestData.Now.AddDays(-1), TestData.Now, "5s", TestData.Now));
        Assert.Throws<ArgumentException>(() => HistoryQuery.Create(TestData.Now, TestData.Now, "auto", TestData.Now));
        Assert.Throws<ArgumentException>(() => HistoryQuery.Create(TestData.Now.AddYears(-3), TestData.Now, "auto", TestData.Now));
    }
}
