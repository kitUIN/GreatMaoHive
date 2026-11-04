using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreatMaoNet.Updates;

namespace GreatMaoNet.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("v1.10.0", "1.9.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v0.9.0", "1.0.0", false)]
    public void VersionsAreComparedNumerically(string latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateOptions.ParseVersion(latest) > UpdateOptions.ParseVersion(current));

    [Theory]
    [InlineData("v1.0.0-beta")]
    [InlineData("v1.0")]
    [InlineData("../1.0.0")]
    [InlineData("v01.0.0")]
    public void RejectsNonStableOrUnsafeVersions(string value) => Assert.Throws<InvalidDataException>(() => UpdateOptions.ParseVersion(value));

    [Fact]
    public async Task SelectsExactApplicationAndPlatformAndDownloadsVerifiedBytes()
    {
        using var directory = new TemporaryDirectory();
        byte[] package = Encoding.UTF8.GetBytes("test binary package");
        var release = Release(package);
        using var http = Client(release, package);
        var client = new ReleaseClient(http, new(), "GreatMaoNetBee", "win-x64");
        var update = await client.CheckAsync("1.0.0", default);
        Assert.NotNull(update);
        Assert.Equal("1.2.0", update.Version);
        await client.DownloadAsync(update, directory.File("package.zip"), default);
        Assert.Equal(package, File.ReadAllBytes(directory.File("package.zip")));
        Assert.Null(await client.CheckAsync("1.2.0", default));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("truncated")]
    [InlineData("cancel")]
    public async Task FailedDownloadPreservesExistingFileAndRemovesPartial(string failure)
    {
        using var directory = new TemporaryDirectory();
        byte[] package = Encoding.UTF8.GetBytes("original");
        var release = Release(package);
        using var http = Client(release, package, failure);
        var client = new ReleaseClient(http, new(), "GreatMaoNetBee", "win-x64");
        var update = await client.CheckAsync("1.0.0", default);
        string path = directory.File("package.zip");
        File.WriteAllText(path, "old");
        await Assert.ThrowsAnyAsync<Exception>(() => client.DownloadAsync(update!, path, default));
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task ValidCachedPackageDoesNotDownloadBinaryAgain()
    {
        using var directory = new TemporaryDirectory();
        byte[] package = Encoding.UTF8.GetBytes("cached package");
        var release = Release(package);
        using var http = Client(release, package, "cancel"); // Any binary request would throw.
        var client = new ReleaseClient(http, new(), "GreatMaoNetBee", "win-x64");
        var update = await client.CheckAsync("1.0.0", default);
        string path = directory.File("package.zip");
        File.WriteAllBytes(path, package);
        await client.DownloadAsync(update!, path, default);
        Assert.Equal(package, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("prerelease")]
    [InlineData("draft")]
    [InlineData("missing")]
    [InlineData("url")]
    [InlineData("large")]
    public async Task RejectsIneligibleReleasesAndUnsafeAssets(string scenario)
    {
        byte[] package = [1, 2, 3];
        var release = Release(package);
        release.Prerelease = scenario == "prerelease";
        release.Draft = scenario == "draft";
        if (scenario == "missing") release.Assets = [];
        if (scenario == "url") release.Assets[0].Url = "https://example.com/package.zip";
        if (scenario == "large") release.Assets[0].Size = ReleaseClient.MaxPackageBytes + 1;
        using var http = Client(release, package);
        var client = new ReleaseClient(http, new(), "GreatMaoNetBee", "win-x64");
        if (scenario is "prerelease" or "draft") Assert.Null(await client.CheckAsync("1.0.0", default));
        else await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync("1.0.0", default));
    }

    [Fact]
    public async Task NoReleaseAndRateLimitAreHandledWithoutDownload()
    {
        using var missing = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.NotFound)));
        Assert.Null(await new ReleaseClient(missing, new(), "GreatMaoNetBee", "win-x64").CheckAsync("1.0.0", default));
        using var limited = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ReleaseClient(limited, new(), "GreatMaoNetBee", "win-x64").CheckAsync("1.0.0", default));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("folder/escape.dll")]
    [InlineData("..\\escape.dll")]
    [InlineData("C:escape.dll")]
    public void RejectsArchiveTraversal(string name)
    {
        using var directory = new TemporaryDirectory();
        string archive = directory.File("test.zip");
        WriteZip(archive, [name]);
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(archive, directory.File("payload"), "GreatMaoNetBee"));
    }

    [Fact]
    public void ExtractAndInstallPreserveConfigurationAndDataAndCanRollback()
    {
        using var directory = new TemporaryDirectory();
        string app = UpdateInstaller.Executable("GreatMaoNetBee");
        string helper = UpdateInstaller.Executable("GreatMaoNetUpdater");
        string archive = directory.File("test.zip");
        WriteZip(archive, [app, helper, "e_sqlite3.dll", "appsettings.json", "appsettings.Local.json", "bee.db"]);
        string payload = directory.File("payload");
        string[] files = UpdateInstaller.Extract(archive, payload, "GreatMaoNetBee");
        Assert.Equal(3, files.Length);
        string install = directory.File("install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, app), "old binary");
        File.WriteAllText(Path.Combine(install, "appsettings.json"), "configuration");
        File.WriteAllText(Path.Combine(install, "bee.db"), "database");
        var transaction = UpdateInstaller.Install(install, new() { Application = "GreatMaoNetBee", Version = "1.2.0", PayloadDirectory = payload, Files = files });
        Assert.Equal("new binary", File.ReadAllText(Path.Combine(install, app)));
        Assert.Equal("configuration", File.ReadAllText(Path.Combine(install, "appsettings.json")));
        Assert.Equal("database", File.ReadAllText(Path.Combine(install, "bee.db")));
        transaction.Rollback();
        Assert.Equal("old binary", File.ReadAllText(Path.Combine(install, app)));
        Assert.False(File.Exists(Path.Combine(install, helper)));
    }

    [Fact]
    public void PartialReplacementFailureRestoresAlreadyChangedFiles()
    {
        using var directory = new TemporaryDirectory();
        string payload = directory.File("payload");
        Directory.CreateDirectory(payload);
        string app = UpdateInstaller.Executable("GreatMaoNetBee");
        File.WriteAllText(Path.Combine(payload, app), "new");
        File.WriteAllText(directory.File(app), "old");
        Assert.Throws<FileNotFoundException>(() => UpdateInstaller.Install(directory.Path, new()
        {
            Application = "GreatMaoNetBee", Version = "1.2.0", PayloadDirectory = payload, Files = [app, "missing.dll"]
        }));
        Assert.Equal("old", File.ReadAllText(directory.File(app)));
    }

    [Fact]
    public void RejectsSymlinksDuplicateEntriesAndMissingBinaries()
    {
        using var directory = new TemporaryDirectory();
        string archive = directory.File("test.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link.dll");
            entry.ExternalAttributes = 0xA000 << 16;
        }
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(archive, directory.File("link"), "GreatMaoNetBee"));
        File.Delete(archive);
        WriteZip(archive, ["a.dll", "A.dll"]);
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(archive, directory.File("duplicate"), "GreatMaoNetBee"));
        File.Delete(archive);
        WriteZip(archive, ["a.dll"]);
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(archive, directory.File("missing"), "GreatMaoNetBee"));
    }

    private static void WriteZip(string path, string[] files)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string name in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("new binary");
        }
    }

    private static GitHubRelease Release(byte[] bytes)
    {
        const string name = "GreatMaoNetBee-win-x64.zip";
        const string url = "https://github.com/kitUIN/GreatMaoHive/releases/download/v1.2.0/";
        return new() { Tag = "v1.2.0", Assets = [new() { Name = name, Url = url + name, Size = bytes.Length },
            new() { Name = name + ".sha256", Url = url + name + ".sha256", Size = 64 + 2 + name.Length + 1 }] };
    }

    private static HttpClient Client(GitHubRelease release, byte[] bytes, string? failure = null) => new(new FakeHandler(request =>
    {
        if (request.RequestUri!.Host == "api.github.com")
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(release, UpdateJson.Default.GitHubRelease)) };
        if (request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            return new(HttpStatusCode.OK) { Content = new StringContent(Convert.ToHexString(SHA256.HashData(bytes)) + "  GreatMaoNetBee-win-x64.zip\n") };
        if (failure == "cancel") throw new OperationCanceledException();
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(failure switch { "checksum" => new byte[bytes.Length], "truncated" => [0], _ => bytes }) };
    }));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
