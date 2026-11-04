using GreatMaoNetBee;

namespace GreatMaoNet.Tests;

public sealed class SamplingTests
{
    [Fact]
    public void FirstReadingIsBaselineThenDeltaUsesMonotonicTime()
    {
        var engine = new SamplingEngine(TestData.Checkpoint());
        Assert.False(engine.Advance([TestData.Counter(1_000_000, 500)], TestData.Now, 0, "boot").Available);
        var result = engine.Advance([TestData.Counter(1_000_200, 600)], TestData.Now.AddSeconds(2), 2, "boot");
        Assert.Equal(800, result.DownloadBitsPerSecond);
        Assert.Equal(400, result.UploadBitsPerSecond);
        Assert.Equal(300, engine.Checkpoint.Totals.TotalBytes);
    }

    [Fact]
    public void RestartSameBootRecoversBytesWithoutInventingPeak()
    {
        var original = new SamplingEngine(TestData.Checkpoint());
        original.Advance([TestData.Counter(1000)], TestData.Now, 0, "boot");
        var restored = new SamplingEngine(original.Checkpoint);
        var current = restored.Advance([TestData.Counter(9000)], TestData.Now.AddMinutes(1), 0, "boot");
        Assert.False(current.Available);
        Assert.Equal("process-restarted", current.GapReason);
        Assert.Equal(8000, restored.Checkpoint.Totals.ReceivedBytes);
        var record = restored.CompleteRecord(TestData.Now);
        Assert.Equal(0, record.PeakDownloadBitsPerSecond);
        Assert.Equal(0, record.ReceivedBytes);
        Assert.True(record.Totals.HasGaps);
    }

    [Theory]
    [InlineData("new-boot", "eth0", 9000, "system-restarted")]
    [InlineData("boot", "eth1", 9000, "interface-changed")]
    [InlineData("boot", "eth0", 10, "counter-reset")]
    public void DiscontinuityRebaselines(string boot, string id, long counter, string reason)
    {
        var engine = new SamplingEngine(TestData.Checkpoint());
        engine.Advance([TestData.Counter(1000)], TestData.Now, 0, "boot");
        var result = engine.Advance([TestData.Counter(counter, id: id)], TestData.Now.AddSeconds(1), 1, boot);
        Assert.Equal(reason, result.GapReason);
        Assert.False(result.Available);
        Assert.Equal(0, engine.Checkpoint.Totals.TotalBytes);
    }

    [Fact]
    public void MissingInterfaceAndClockChangeAreVisibleGaps()
    {
        var engine = new SamplingEngine(TestData.Checkpoint());
        engine.Advance([TestData.Counter(1000)], TestData.Now, 0, "boot");
        var clock = engine.Advance([TestData.Counter(1100)], TestData.Now.AddHours(-1), 1, "boot");
        Assert.Equal("clock-adjusted", clock.GapReason);
        Assert.Equal(100, engine.Checkpoint.Totals.ReceivedBytes);
        Assert.Equal("no-interface", engine.Advance([], TestData.Now, 2, "boot").GapReason);
    }

    [Fact]
    public void AggregatePeakUsesSimultaneousTrafficRatherThanAddingIndependentPeaks()
    {
        var engine = new SamplingEngine(TestData.Checkpoint());
        engine.Advance([TestData.Counter(0), TestData.Counter(0, id: "eth1")], TestData.Now, 0, "boot");
        engine.Advance([TestData.Counter(100), TestData.Counter(0, id: "eth1")], TestData.Now.AddSeconds(1), 1, "boot");
        engine.Advance([TestData.Counter(100), TestData.Counter(100, id: "eth1")], TestData.Now.AddSeconds(2), 2, "boot");
        Assert.Equal(800, engine.CompleteRecord(TestData.Now).PeakDownloadBitsPerSecond);
    }

    [Fact]
    public void LinuxRoutesPreferIpv4AndLowestMetricAndIgnoreRejectedRoutes()
    {
        string[] v4 = ["eth0 00000000 0100000A 0003 0 0 100 00000000", "eth1 00000000 0100000A 0003 0 0 5 00000000"];
        string v6 = "00000000000000000000000000000000 00 00000000000000000000000000000000 00 00000000000000000000000000000001 00000001 00000000 00000000 00000003 eth2";
        Assert.Equal("eth1", NetworkCounterSource.ParseLinuxDefaultRoute(v4, [v6]));
        Assert.Equal("eth2", NetworkCounterSource.ParseLinuxDefaultRoute([], [v6]));
        Assert.Null(NetworkCounterSource.ParseLinuxDefaultRoute(["eth0 00000000 0100000A 0201 0 0 0 00000000"], []));
    }
}
