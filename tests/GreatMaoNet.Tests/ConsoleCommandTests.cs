using System.Net;
using System.Text;
using GreatMaoNet.Updates;
using GreatMaoNetBee;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GreatMaoNet.Tests;

public sealed class ConsoleCommandTests
{
    [Fact]
    public async Task CommandsAreCaseInsensitiveAndDoNotStopMonitoringUntilExit()
    {
        using var lifetime = new TestLifetime();
        using var output = new StringWriter();
        var updates = new TestUpdates();
        var commands = new ConsoleCommandProcessor("GreatMaoNetBee", new TestStatus(), updates, lifetime, output);
        foreach (string command in new[] { "help", " STATUS ", "connections", "version", "check-update", "download-update", "rm -rf /", "" })
            await commands.ExecuteAsync(command, default);
        Assert.Equal(new[] { false, true }, updates.Downloads);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Contains("Hive connection: Connected", output.ToString());
        Assert.Contains("Unknown command", output.ToString());
        Assert.Contains("GreatMaoNetBee", output.ToString());
        await commands.ExecuteAsync("QUIT", default);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task FailedUpdateCheckDoesNotDisableOtherCommands()
    {
        using var lifetime = new TestLifetime();
        using var output = new StringWriter();
        var commands = new ConsoleCommandProcessor("GreatMaoNetHive", new TestStatus(), new TestUpdates { Fail = true }, lifetime, output);
        await commands.ExecuteAsync("check-update", default);
        await commands.ExecuteAsync("status", default);
        Assert.Contains("Command failed", output.ToString());
        Assert.Contains("Hive connection: Connected", output.ToString());
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task EndOfInputDoesNotStopTheApplication()
    {
        using var lifetime = new TestLifetime();
        using var output = new StringWriter();
        using var input = new StringReader("status\n");
        var commands = new ConsoleCommandProcessor("GreatMaoNetBee", new TestStatus(), new TestUpdates(), lifetime, output);
        using var service = new ConsoleCommandService(input, output, commands, lifetime);
        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("Hive connection: Connected", output.ToString());
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task ShutdownDoesNotWaitForTerminalInput()
    {
        using var lifetime = new TestLifetime();
        using var output = new StringWriter();
        using var input = new BlockingReader();
        var commands = new ConsoleCommandProcessor("GreatMaoNetBee", new TestStatus(), new TestUpdates(), lifetime, output);
        using var service = new ConsoleCommandService(input, output, commands, lifetime);
        try
        {
            await service.StartAsync(default);
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(service.ExecuteTask!.IsCompleted);
        }
        finally { input.Release.Set(); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ServiceModeOrExplicitDisableDoesNotRegisterConsoleReader(bool windowsService, bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Console:Enabled"] = enabled.ToString() }).Build();
        var services = new ServiceCollection();
        services.AddConsoleCommands(configuration, "GreatMaoNetBee", windowsService);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void BeeStatusTracksHandshakeTimeoutRecoveryAndDisconnectWithoutExposingToken()
    {
        var clock = new TestClock();
        var connection = new BeeConnectionState(clock);
        var options = new BeeOptions { Id = "bee-test", Name = "Test", Token = "private-token" };
        var provider = new BeeConsoleStatus(options, connection);
        Assert.Equal("Disconnected", connection.Snapshot().State);
        connection.Connecting();
        Assert.Equal("Connecting", connection.Snapshot().State);
        connection.Connected();
        clock.Advance(TimeSpan.FromSeconds(44));
        Assert.Equal("Connected", connection.Snapshot().State);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Timed out", connection.Snapshot().State);
        connection.Received();
        Assert.Equal("Connected", connection.Snapshot().State);
        connection.Disconnected();
        Assert.Equal("Disconnected", connection.Snapshot().State);
        Assert.DoesNotContain(options.Token, string.Join("\n", provider.GetStatus()));
        connection.Connecting();
        Assert.Equal("Connecting", connection.Snapshot().State);
    }

    [Fact]
    public async Task ManualCheckReportsUnavailableReleaseInsteadOfClaimingUpToDate()
    {
        using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.NotFound)));
        using var commands = new ReleaseCommands(new(), "GreatMaoNetBee", http);
        using var output = new StringWriter();
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => commands.CheckAsync("0.1.0", false, output, default));
        Assert.Contains("private", exception.Message);
        Assert.DoesNotContain("no newer", output.ToString());
    }

    [Fact]
    public async Task ManualCheckDoesNotDownloadAndDownloadHonorsPendingUpdateLock()
    {
        using var directory = new TemporaryDirectory();
        int requests = 0;
        string name = $"GreatMaoNetBee-{UpdateOptions.Runtime}.zip";
        string metadata = $$"""
            {"tag_name":"v1.2.0","draft":false,"prerelease":false,"assets":[
            {"name":"{{name}}","size":3,"browser_download_url":"https://github.com/kitUIN/GreatMaoHive/releases/download/v1.2.0/{{name}}"},
            {"name":"{{name}}.sha256","size":100,"browser_download_url":"https://github.com/kitUIN/GreatMaoHive/releases/download/v1.2.0/{{name}}.sha256"}]}
            """;
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            requests++;
            Assert.EndsWith("/releases/latest", request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent(metadata, Encoding.UTF8, "application/json") };
        }));
        var options = new UpdateOptions { Enabled = false, AutoDownload = false, AutoApply = false, DownloadDirectory = directory.Path };
        using var commands = new ReleaseCommands(options, "GreatMaoNetBee", http);
        using var output = new StringWriter();
        await commands.CheckAsync("0.1.0", false, output, default);
        Assert.Equal(1, requests);
        Assert.Empty(Directory.GetFiles(directory.Path));
        Assert.Contains("0.1.0 -> 1.2.0", output.ToString());
        File.WriteAllText(directory.File("pending.json"), "pending update");
        await Assert.ThrowsAsync<IOException>(() => commands.CheckAsync("0.1.0", true, output, default));
        Assert.Equal(2, requests);
        Assert.Equal("pending update", File.ReadAllText(directory.File("pending.json")));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.zip"));
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
    private sealed class TestStatus : IConsoleStatusProvider
    {
        public IEnumerable<string> GetStatus() => ["Hive connection: Connected"];
    }
    private sealed class TestUpdates : IReleaseCommands
    {
        public bool Fail { get; init; }
        public List<bool> Downloads { get; } = [];
        public Task CheckAsync(string currentVersion, bool download, TextWriter output, CancellationToken token)
        {
            Downloads.Add(download);
            if (Fail) throw new HttpRequestException("Network unavailable");
            return Task.CompletedTask;
        }
    }
    private sealed class BlockingReader : TextReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public override string? ReadLine() { Started.TrySetResult(); Release.Wait(); return null; }
    }
    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();
        public TestLifetime() => started.Cancel();
        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() { started.Dispose(); stopping.Dispose(); stopped.Dispose(); }
    }
}
