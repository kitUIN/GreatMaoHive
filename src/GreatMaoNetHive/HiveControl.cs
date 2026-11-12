using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GreatMaoNetHive;

public sealed record HiveControlRequest(string Command, string BeeId);

[JsonSerializable(typeof(HiveControlRequest))]
[JsonSerializable(typeof(BeeUpdateCommandResult))]
public partial class HiveControlJson : JsonSerializerContext;

public static class HiveControlProtocol
{
    public const int MaximumMessageBytes = 4096;

    public static string PipeName(string? configured, string installationDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationDirectory));
        // Keep the Linux socket inside the installation directory permitted by our systemd unit.
        if (!OperatingSystem.IsWindows()) return Path.Combine(directory, ".hive-control.sock");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        return "GreatMaoNetHive-" + hash[..24] + "-control";
    }

    public static async Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Control message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid control message length.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize(payload, type) ?? throw new InvalidDataException("Empty control message.");
    }
}

public sealed class HiveControlService(HiveOptions options, BeeUpdateCommands commands, ILogger<HiveControlService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string name = HiveControlProtocol.PipeName(options.ControlPipeName, AppContext.BaseDirectory);
        try
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            while (!stoppingToken.IsCancellationRequested)
            {
                bool connected = false;
                try
                {
                    await server.WaitForConnectionAsync(stoppingToken);
                    connected = true;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var request = await HiveControlProtocol.ReadAsync(server, HiveControlJson.Default.HiveControlRequest, timeout.Token);
                    var result = request.Command == "update-bee" && ValidBeeId(request.BeeId)
                        ? await commands.UpdateAsync(request.BeeId, timeout.Token)
                        : new BeeUpdateCommandResult(400, null, "Expected update-bee and a valid Bee ID.");
                    await HiveControlProtocol.WriteAsync(server, result, HiveControlJson.Default.BeeUpdateCommandResult, timeout.Token);
                    // Windows Disconnect can discard unread data; wait until the client has read the reply.
                    await server.ReadExactlyAsync(new byte[1], timeout.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException)
                { logger.LogWarning("Local Hive command interrupted: {Message}", exception.Message); }
                finally { if (connected) server.Disconnect(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        { logger.LogError(exception, "Local Hive commands unavailable; check Hive:ControlPipeName and installation directory permissions. Monitoring continues."); }
    }

    public static bool ValidBeeId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);
}

public static class HiveControlCommand
{
    public static async Task<int?> HandleAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        if (!args.Any(arg => arg == "--update-bee" || arg.StartsWith("--update-bee=", StringComparison.Ordinal))) return null;
        string? id = null;
        var configurationArgs = new List<string>();
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg == "--update-bee" || arg.StartsWith("--update-bee=", StringComparison.Ordinal))
            {
                if (id is not null) return await UsageAsync(error);
                if (arg == "--update-bee") id = ++index < args.Length ? args[index] : null;
                else id = arg["--update-bee=".Length..];
                if (!HiveControlService.ValidBeeId(id) || id!.StartsWith("--", StringComparison.Ordinal)) return await UsageAsync(error);
            }
            else if (arg is "--version" or "--check-update" or "--download-update") return await UsageAsync(error);
            else configurationArgs.Add(arg);
        }

        try
        {
            var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true).AddJsonFile("appsettings.Local.json", optional: true)
                .AddEnvironmentVariables().AddCommandLine(configurationArgs.ToArray()).Build();
            using var configurationLifetime = configuration as IDisposable;
            string name = HiveControlProtocol.PipeName(configuration["Hive:ControlPipeName"], AppContext.BaseDirectory);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await client.ConnectAsync(3000, token); }
            catch (TimeoutException)
            {
                await error.WriteLineAsync("Cannot connect to the running Hive. Start the updated Hive service and run this command as the same service account; check Hive:ControlPipeName if customized.");
                return 1;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await HiveControlProtocol.WriteAsync(client, new HiveControlRequest("update-bee", id!), HiveControlJson.Default.HiveControlRequest, timeout.Token);
            var result = await HiveControlProtocol.ReadAsync(client, HiveControlJson.Default.BeeUpdateCommandResult, timeout.Token);
            await client.WriteAsync(new byte[] { 1 }, timeout.Token);
            await client.FlushAsync(timeout.Token);
            if (result.StatusCode == 202 && result.Response is { Status: "accepted" } accepted && accepted.Id == id)
            {
                await output.WriteLineAsync($"Bee {id}: update accepted (request {accepted.RequestId}). This confirms acceptance, not completion; check the Bee service logs for the result.");
                return 0;
            }
            await error.WriteLineAsync($"Bee {id}: update request failed ({result.StatusCode}): {result.Error ?? "Invalid acknowledgement."}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Hive command cancelled or timed out; update execution may be unknown. Check the Bee service logs before retrying.");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            await error.WriteLineAsync($"Hive command failed: {exception.Message}. Use the same account as the Hive service; if the request was sent, update execution may be unknown.");
            return 1;
        }
    }

    private static async Task<int> UsageAsync(TextWriter error)
    {
        await error.WriteLineAsync("Usage: GreatMaoNetHive --update-bee <bee-id> [--Hive:ControlPipeName <name-or-unix-path>]");
        return 2;
    }
}
