using GreatMaoNet.Contracts;
using GreatMaoNetHive;

namespace GreatMaoNet.Tests;

public sealed class QuotaTests
{
    [Theory]
    [InlineData("2026-11-01T00:00:00+08:00", "monthly", 1, 8, "2026-10-31T16:00:00Z", "2026-11-30T16:00:00Z")]
    [InlineData("2026-10-31T23:59:59+08:00", "monthly", 1, 8, "2026-09-30T16:00:00Z", "2026-10-31T16:00:00Z")]
    [InlineData("2026-02-28T12:00:00Z", "monthly", 31, 0, "2026-02-28T00:00:00Z", "2026-03-31T00:00:00Z")]
    [InlineData("2024-02-28T12:00:00Z", "monthly", 31, 0, "2024-01-31T00:00:00Z", "2024-02-29T00:00:00Z")]
    [InlineData("2026-01-01T12:00:00Z", "monthly", 15, 0, "2025-12-15T00:00:00Z", "2026-01-15T00:00:00Z")]
    [InlineData("2026-11-08T00:00:00Z", "weekly", 1, 0, "2026-11-02T00:00:00Z", "2026-11-09T00:00:00Z")]
    [InlineData("2026-11-08T00:00:00Z", "weekly", 7, 0, "2026-11-08T00:00:00Z", "2026-11-15T00:00:00Z")]
    [InlineData("2026-11-07T01:00:00Z", "daily", 1, -5, "2026-11-06T05:00:00Z", "2026-11-07T05:00:00Z")]
    public void CalendarBoundariesAreStable(string timestamp, string period, int day, int offset, string start, string end)
    {
        var policy = new TrafficQuotaOptions { Period = period, ResetDay = day, UtcOffsetHours = offset };
        var actual = policy.GetPeriod(DateTimeOffset.Parse(timestamp));
        Assert.Equal(DateTimeOffset.Parse(start), actual.Start);
        Assert.Equal(DateTimeOffset.Parse(end), actual.End);
    }

    [Theory]
    [InlineData(-1, "monthly", 1, 0)]
    [InlineData(0, "yearly", 1, 0)]
    [InlineData(0, "monthly", 32, 0)]
    [InlineData(0, "weekly", 8, 0)]
    [InlineData(0, "daily", 2, 0)]
    [InlineData(0, "monthly", 0, 0)]
    [InlineData(0, "monthly", 1, 15)]
    public void InvalidPoliciesAreRejected(long limit, string period, int day, int offset) =>
        Assert.Throws<InvalidOperationException>(() => new TrafficQuotaOptions
            { LimitBytes = limit, Period = period, ResetDay = day, UtcOffsetHours = offset }.Validate());

    [Fact]
    public void PeriodTotalsSurviveReplayPruningRestartAndPolicyChanges()
    {
        using var directory = new TemporaryDirectory();
        var boundary = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var now = boundary.AddDays(15);
        var node = TestData.Node();
        var bee = new BeeCredential { Id = node.Id, Quota = new() { LimitBytes = 299 } };
        using (var store = TestData.Store(directory))
        {
            store.Register(new(node, 0, 0), now);
            var batch = new BatchMessage([Record(1, boundary.AddSeconds(-5), boundary.AddDays(-2)),
                Record(2, boundary, boundary.AddDays(-2)), Record(3, boundary.AddSeconds(5), boundary.AddDays(-2))], 0);
            store.Ingest(node.Id, batch, now);
            store.Ingest(node.Id, batch, now);
            store.Prune(now); // Original five-second buckets have already expired.
            var usage = Assert.Single(store.TrafficQuotas([bee], now).Bees);
            Assert.Equal(300, usage.TotalBytes);
            Assert.Equal(200, usage.ReceivedBytes);
            Assert.Equal(100, usage.SentBytes);
            Assert.True(usage.IsExceeded);
            Assert.Equal(0, usage.RemainingBytes);
            Assert.False(usage.HasGaps); // Complete up to lastSampledAt, although the node is stale.
            Assert.Equal(boundary.AddSeconds(10), usage.LastSampledAt);
        }
        using (var store = TestData.Store(directory))
        {
            Assert.Equal(300, Assert.Single(store.TrafficQuotas([bee], now).Bees).TotalBytes);
            bee.Quota.LimitBytes = 300;
            Assert.False(Assert.Single(store.TrafficQuotas([bee], now).Bees).IsExceeded);
            bee.Quota.ResetDay = 2;
            Assert.Equal(450, Assert.Single(store.TrafficQuotas([bee], boundary.AddHours(1)).Bees).TotalBytes);
            bee.Quota.ResetDay = 1;
            Assert.Null(Assert.Single(store.TrafficQuotas([bee], boundary.AddMonths(1)).Bees).TotalBytes);
        }
    }

    [Fact]
    public void UnlimitedUnseenAndObservedZeroAreDistinctAndRecoveredBytesAreNotInvented()
    {
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var start = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var node = TestData.Node();
        store.Register(new(node, 0, 0), start);
        store.Ingest(node.Id, new([Record(1, start, start) with
        {
            ReceivedBytes = 0, SentBytes = 0, Totals = new(start, 0, 0, false, null)
        }], 0), start.AddSeconds(5));
        var bees = new[] { new BeeCredential { Id = node.Id }, new BeeCredential { Id = "unseen", Quota = new() { LimitBytes = 0 } } };
        var result = store.TrafficQuotas(bees, start.AddSeconds(5));
        var known = result.Bees.Single(x => x.Id == node.Id);
        Assert.Equal(0, known.TotalBytes);
        Assert.False(known.HasGaps);
        Assert.Null(known.RemainingBytes);
        Assert.Null(known.IsExceeded);
        Assert.Null(result.Bees.Single(x => x.Id == "unseen").TotalBytes);
        store.Ingest(node.Id, new([Record(2, start.AddSeconds(10), start) with
        {
            Totals = new(start, 1000, 500, true, start.AddSeconds(10))
        }], 0), start.AddSeconds(15));
        known = store.TrafficQuotas(bees, start.AddSeconds(15)).Bees.Single(x => x.Id == node.Id);
        Assert.Equal(150, known.TotalBytes);
        Assert.True(known.HasGaps);
    }

    private static TrafficRecord Record(long sequence, DateTimeOffset start, DateTimeOffset since) =>
        TestData.Record(sequence, start) with { Totals = new(since, sequence * 100, sequence * 50, false, null) };
}
