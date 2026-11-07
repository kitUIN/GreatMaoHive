using System.Text.Json;
using GreatMaoNet.Contracts;
using GreatMaoNetBee;
using Microsoft.Data.Sqlite;

namespace GreatMaoNet.Tests;

public sealed class NodeMetadataTests
{
    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("  ", "  ", null, null)]
    [InlineData(" 中国香港 ", " 192.0.2.10 ", "中国香港", "192.0.2.10")]
    [InlineData("Tokyo", "2001:0DB8:0:0:0:0:0:10", "Tokyo", "2001:db8::10")]
    [InlineData("Local", "::1", "Local", "::1")]
    public void ConfigurationAndRegistrationNormalizeOptionalMetadata(string? region, string? ip,
        string? expectedRegion, string? expectedIp)
    {
        var options = Options(region, ip);
        options.Validate();
        Assert.Equal(expectedRegion, options.Region);
        Assert.Equal(expectedIp, options.Ip);
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var node = TestData.Node() with { Region = region, Ip = ip };
        store.Register(new(node, 0, 0), TestData.Now);
        Assert.Equal(expectedRegion, store.Get(node.Id)!.Node.Region);
        Assert.Equal(expectedIp, store.Get(node.Id)!.Node.Ip);
    }

    [Theory]
    [InlineData("hostname.example")]
    [InlineData("999.1.2.3")]
    [InlineData("192.0.2.10:80")]
    [InlineData("[2001:db8::10]")]
    [InlineData("fe80::1%eth0")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f.0.0.1")]
    public void InvalidIpIsRejectedByBothBeeAndHive(string ip)
    {
        Assert.Throws<InvalidOperationException>(() => Options("Hong Kong", ip).Validate());
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var node = TestData.Node() with { Region = "Hong Kong", Ip = "192.0.2.10" };
        store.Register(new(node, 0, 0), TestData.Now);
        Assert.Throws<InvalidDataException>(() => store.Register(new(node with { Ip = ip }, 0, 0), TestData.Now));
        Assert.Equal(node, store.Get(node.Id)!.Node with { Interfaces = node.Interfaces });
    }

    [Fact]
    public void RegionLengthLimitIsCheckedOnBothSides()
    {
        Options(new string('地', 256), null).Validate();
        Assert.Throws<InvalidOperationException>(() => Options(new string('地', 257), null).Validate());
        using var directory = new TemporaryDirectory();
        using var store = TestData.Store(directory);
        var node = TestData.Node() with { Region = new string('地', 256) };
        store.Register(new(node, 0, 0), TestData.Now);
        Assert.Throws<InvalidDataException>(() => store.Register(new(node with { Region = new string('地', 257) }, 0, 0), TestData.Now));
    }

    [Fact]
    public void MetadataSurvivesUploadAndRestartAndCanBeChangedOrClearedWithoutLosingTraffic()
    {
        using var directory = new TemporaryDirectory();
        var node = TestData.Node() with { Region = "中国香港", Ip = "192.0.2.10" };
        using (var store = TestData.Store(directory))
        {
            store.Register(new(node, 0, 0), TestData.Now);
            store.Ingest(node.Id, new([TestData.Record(1)], 0), TestData.Now.AddSeconds(5));
        }
        using (var store = TestData.Store(directory))
        {
            Assert.Equal(node.Region, store.Get(node.Id)!.Node.Region);
            Assert.Equal(node.Ip, store.Get(node.Id)!.Node.Ip);
            node = node with { Region = "Tokyo", Ip = "2001:db8::10" };
            Assert.Equal(1, store.Register(new(node, 1, 1), TestData.Now.AddSeconds(10)));
        }
        using (var store = TestData.Store(directory))
        {
            Assert.Equal(node.Region, store.Get(node.Id)!.Node.Region);
            Assert.Equal(node.Ip, store.Get(node.Id)!.Node.Ip);
            store.Register(new(node with { Region = " ", Ip = "" }, 1, 1), TestData.Now.AddSeconds(15));
        }
        using (var store = TestData.Store(directory))
        {
            var saved = store.Get(node.Id)!;
            Assert.Null(saved.Node.Region);
            Assert.Null(saved.Node.Ip);
            Assert.Equal(1, saved.Sequence);
            Assert.Equal(150, saved.Totals!.TotalBytes);
        }
    }

    [Fact]
    public void LegacyDatabaseAndHelloWithoutMetadataRemainReadable()
    {
        const string legacyNode = """
            {"id":"bee-01","name":"Legacy Bee","operatingSystem":"test",
             "dataEpoch":"00112233445566778899aabbccddeeff","interfaces":[]}
            """;
        using var directory = new TemporaryDirectory();
        using (var store = TestData.Store(directory))
        {
            var hello = JsonSerializer.Deserialize("{\"type\":\"hello\",\"hello\":{\"node\":" + legacyNode +
                ",\"lastAcknowledgedSequence\":0,\"lastSequence\":0}}", ProtocolJson.Default.WireMessage)!.Hello!;
            Assert.Null(hello.Node.Region);
            Assert.Null(hello.Node.Ip);
            store.Register(hello, TestData.Now);
            store.Ingest(hello.Node.Id, new([TestData.Record(1)], 0), TestData.Now.AddSeconds(5));
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.File("hive.db") }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE nodes SET node_json=$node WHERE id='bee-01'";
            command.Parameters.AddWithValue("$node", legacyNode);
            command.ExecuteNonQuery();
        }
        using var reopened = TestData.Store(directory);
        var saved = reopened.Get("bee-01")!;
        Assert.Null(saved.Node.Region);
        Assert.Null(saved.Node.Ip);
        Assert.Equal(150, saved.Totals!.TotalBytes);
        Assert.Single(reopened.List());
    }

    private static BeeOptions Options(string? region, string? ip) => new()
    {
        Id = "bee-01", Token = ApiFixture.BeeKey, Region = region, Ip = ip
    };
}
