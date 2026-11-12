using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using GreatMaoNet.Contracts;
using GreatMaoNet.Updates;
using GreatMaoNetBee;
using GreatMaoNetHive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreatMaoNet.Tests;

public sealed class HiveControlTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateCommandProcessNotifiesRealBeeWithoutAdminKeyOrStartingAnotherHive(bool equalsSyntax)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        fixture.Application.Services.GetRequiredService<HiveOptions>().AdminApiKey = "";
        using var directory = new TemporaryDirectory();
        using var workingDirectory = new TemporaryDirectory();
        string id = equalsSyntax ? "socket-51" : "socket-50";
        var options = new BeeOptions
        {
            Id = id, Name = "CLI Bee", DatabasePath = directory.File("bee.db"),
            HiveUrl = new UriBuilder(fixture.Address) { Scheme = "ws", Path = "/agent" }.Uri.ToString(),
            Token = $"{id}-test-credential".PadRight(40, 'x'), AllowInsecureTransport = true
        };
        using var queue = new UpdateRequestQueue(new UpdateOptions { Enabled = false });
        queue.SetAvailable(true);
        using var store = new BeeStore(options);
        using var sampling = new SamplingService(store, new EmptyCounterSource(), NullLogger<SamplingService>.Instance);
        using var bee = new HiveConnectionService(options, store, sampling, new BeeConnectionState(), NullLogger<HiveConnectionService>.Instance, queue);
        await bee.StartAsync(token);
        try
        {
            var sessions = fixture.Application.Services.GetRequiredService<BeeSessions>();
            while (sessions.Get(id)?.Online != true) await Task.Delay(10, token);
            string[] arguments = equalsSyntax ? ["--update-bee=" + id] : ["--update-bee", id];
            var result = await RunProcessAsync([.. arguments, "--Hive:ControlPipeName", fixture.ControlPipeName], workingDirectory.Path, token);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains($"Bee {id}: update accepted", result.Output);
            Assert.Contains("not completion", result.Output);
            Assert.Empty(result.Error);
            Assert.True(await queue.WaitAsync(TimeSpan.Zero, token));
            Assert.Empty(Directory.GetFileSystemEntries(workingDirectory.Path));
            Assert.Equal("ok", System.Text.Json.JsonSerializer.Deserialize(
                await fixture.Client.GetStringAsync("/healthz", token), ProtocolJson.Default.HealthResponse)!.Status);
            await bee.PrepareAsync(token);
            await bee.CancelAsync(token);
        }
        finally { await bee.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("missing", "404", "not found")]
    [InlineData("socket-98", "503", "offline")]
    public async Task FailedCommandHasNonzeroExitAndUsefulMessage(string id, string status, string message)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int? code = await HiveControlCommand.HandleAsync(["--update-bee", id, "--Hive:ControlPipeName", fixture.ControlPipeName], output, error);
        Assert.Equal(1, code);
        Assert.Empty(output.ToString());
        Assert.Contains(status, error.ToString());
        Assert.Contains(message, error.ToString());
    }

    [Fact]
    public async Task MalformedLocalClientDoesNotBreakFollowingCommands()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var client = new NamedPipeClientStream(".", fixture.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(timeout.Token);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, HiveControlProtocol.MaximumMessageBytes + 1);
            await client.WriteAsync(header, timeout.Token);
            byte[] reply = new byte[1];
            try { Assert.Equal(0, await client.ReadAsync(reply, timeout.Token)); }
            catch (IOException) { /* Windows reports the server disconnect as a broken pipe. */ }
        }
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await HiveControlCommand.HandleAsync(["--update-bee", "missing", "--Hive:ControlPipeName", fixture.ControlPipeName], output, error, timeout.Token));
        Assert.Contains("404", error.ToString());
    }

    [Fact]
    public async Task ClientDisconnectBeforeRequestDoesNotBreakFollowingCommands()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var client = new NamedPipeClientStream(".", fixture.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            await client.ConnectAsync(timeout.Token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await HiveControlCommand.HandleAsync(["--update-bee", "missing", "--Hive:ControlPipeName", fixture.ControlPipeName], output, error, timeout.Token));
        Assert.Contains("404", error.ToString());
    }

    [Fact]
    public async Task InvalidInvocationExitsBeforeStartingAHost()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var directory = new TemporaryDirectory();
        var result = await RunProcessAsync(["--update-bee="], directory.Path, timeout.Token);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage:", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    internal static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string[] args, string workingDirectory, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, UpdateInstaller.Executable("GreatMaoNetHive")))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = workingDirectory
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["Hive__AdminApiKey"] = "";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private sealed class EmptyCounterSource : ICounterSource
    {
        public string BootId => "test";
        public CounterReading[] Read(double monotonicSeconds) => [];
    }
}

public sealed class HiveControlCommandTests
{
    [Theory]
    [InlineData("--update-bee", "")]
    [InlineData("--update-bee", "--check-update")]
    [InlineData("--update-bee", "bee", "--version")]
    [InlineData("--update-bee=bee", "--update-bee=other")]
    [InlineData("--update-bee=")]
    [InlineData("--update-bee", "bee\nother")]
    [InlineData("--update-bee", "--update-bee")]
    [InlineData("--update-bee", "--bee", "bee-01")]
    [InlineData("--update-bee", "bee-01", "bee-02")]
    [InlineData("--update-bee", "--Hive:ControlPipeName")]
    public async Task InvalidArgumentsReturnUsage(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await HiveControlCommand.HandleAsync(args, output, error));
        Assert.Contains("Usage:", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task OtherCommandsAreLeftToExistingHandlers()
    {
        using var output = new StringWriter();
        Assert.Null(await HiveControlCommand.HandleAsync(["--check-update"], output, output));
        Assert.Null(await HiveControlCommand.HandleAsync([], output, output));
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task MissingServiceReturnsActionableFailure()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string pipe = OperatingSystem.IsWindows() ? "gm-" + Guid.NewGuid().ToString("N")
            : Path.Combine(Path.GetTempPath(), "gm-" + Guid.NewGuid().ToString("N") + ".sock");
        Assert.Equal(1, await HiveControlCommand.HandleAsync(["--update-bee", "bee", "--Hive:ControlPipeName", pipe], output, error));
        Assert.Contains("Cannot connect", error.ToString());
        Assert.Contains("same service account", error.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task OversizedOrInvalidFramesAreRejectedBeforeAllocatingPayload(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => HiveControlProtocol.ReadAsync(stream, HiveControlJson.Default.HiveControlRequest, default));
    }

    [Fact]
    public void DefaultPipeIsStablePerInstallationAndUsesWritableLinuxDirectory()
    {
        string directory = Path.GetFullPath("hive-test-install");
        string name = HiveControlProtocol.PipeName(null, directory);
        Assert.Equal(name, HiveControlProtocol.PipeName(null, directory + Path.DirectorySeparatorChar));
        Assert.NotEqual(name, HiveControlProtocol.PipeName(null, directory + "-other"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(directory, Path.GetDirectoryName(name));
    }
}
