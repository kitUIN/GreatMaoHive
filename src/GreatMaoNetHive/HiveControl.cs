using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GreatMaoNetHive;

public sealed record HiveControlRequest(string Command, string? BeeId);
public sealed record HiveBatchUpdateMessage(string Type, BeeBatchUpdateResult? Item = null, int Accepted = 0, int Failed = 0, int Skipped = 0);

[JsonSerializable(typeof(HiveControlRequest))]
[JsonSerializable(typeof(BeeUpdateCommandResult))]
[JsonSerializable(typeof(HiveBatchUpdateMessage))]
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
                    if (request.Command == "update-all-bees" && request.BeeId is null)
                    {
                        await UpdateAllAsync(server, stoppingToken);
                        continue;
                    }
                    var result = request.Command == "update-bee" && ValidBeeId(request.BeeId)
                        ? await commands.UpdateAsync(request.BeeId!, timeout.Token)
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

    private async Task UpdateAllAsync(Stream stream, CancellationToken token)
    {
        int accepted = 0, failed = 0, skipped = 0;
        await SendAsync(new("batch-start"));
        await foreach (var item in commands.UpdateAllAsync(token))
        {
            if (item.Skipped) skipped++;
            else if (item.Result.StatusCode == 202) accepted++;
            else failed++;
            await SendAsync(new("batch-item", item));
        }
        await SendAsync(new("batch-complete", Accepted: accepted, Failed: failed, Skipped: skipped));

        async Task SendAsync(HiveBatchUpdateMessage message)
        {
            // Each reply has its own deadline and frame, independent of the number of nodes.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await HiveControlProtocol.WriteAsync(stream, message, HiveControlJson.Default.HiveBatchUpdateMessage, timeout.Token);
            await stream.ReadExactlyAsync(new byte[1], timeout.Token);
        }
    }
}

public static class HiveControlCommand
{
    public static async Task<int?> HandleAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        if (!args.Any(arg => arg == "--update-bee" || arg.StartsWith("--update-bee=", StringComparison.Ordinal))) return null;
        string? id = null;
        bool updateSeen = false;
        var configurationArgs = new List<string>();
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg == "--update-bee" || arg.StartsWith("--update-bee=", StringComparison.Ordinal))
            {
                if (updateSeen) return await UsageAsync(error);
                updateSeen = true;
                if (arg == "--update-bee")
                {
                    if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) id = args[++index];
                }
                else
                {
                    id = arg["--update-bee=".Length..];
                    if (id.Length == 0) return await UsageAsync(error);
                }
                if (id is not null && (!HiveControlService.ValidBeeId(id) || id.StartsWith("--", StringComparison.Ordinal))) return await UsageAsync(error);
            }
            else if (arg.Equals("--Hive:ControlPipeName", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal)) return await UsageAsync(error);
                configurationArgs.Add(arg);
                configurationArgs.Add(args[index]);
            }
            else if (arg.StartsWith("--Hive:ControlPipeName=", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(arg.Split('=', 2)[1]))
                configurationArgs.Add(arg);
            else return await UsageAsync(error);
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
            await HiveControlProtocol.WriteAsync(client, new HiveControlRequest(id is null ? "update-all-bees" : "update-bee", id), HiveControlJson.Default.HiveControlRequest, timeout.Token);
            if (id is null) return await ReadBatchAsync(client, output, error, token);
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
        await error.WriteLineAsync("Usage: GreatMaoNetHive --update-bee [bee-id] [--Hive:ControlPipeName <name-or-unix-path>] (omit bee-id to update all online Bees)");
        return 2;
    }

    private static async Task<int> ReadBatchAsync(Stream stream, TextWriter output, TextWriter error, CancellationToken token)
    {
        int accepted = 0, failed = 0, skipped = 0;
        if ((await ReceiveAsync()).Type != "batch-start")
            throw new InvalidDataException("The running Hive does not support batch updates; upgrade and restart the Hive service first.");
        await output.WriteLineAsync("Requesting updates for all currently online Bees...");
        while (true)
        {
            var message = await ReceiveAsync();
            if (message.Type == "batch-complete")
            {
                if (message.Accepted != accepted || message.Failed != failed || message.Skipped != skipped)
                    throw new InvalidDataException("Incomplete batch acknowledgement; check the Bee service logs before retrying.");
                await output.WriteLineAsync($"Batch complete: {accepted} accepted, {failed} failed, {skipped} offline skipped. Acceptance does not confirm installation.");
                if (accepted == 0 && failed == 0) await error.WriteLineAsync("No online Bees to update.");
                return accepted > 0 && failed == 0 ? 0 : 1;
            }
            if (message.Type != "batch-item" || message.Item is not { Result: not null } item || !HiveControlService.ValidBeeId(item.Id))
                throw new InvalidDataException("Invalid batch acknowledgement.");
            if (item.Skipped)
            {
                skipped++;
                await output.WriteLineAsync($"Bee {item.Id}: offline; skipped.");
            }
            else if (item.Result.StatusCode == 202 && item.Result.Response is { Status: "accepted" } response && response.Id == item.Id)
            {
                accepted++;
                await output.WriteLineAsync($"Bee {item.Id}: update accepted (request {response.RequestId}).");
            }
            else
            {
                failed++;
                await error.WriteLineAsync($"Bee {item.Id}: update request failed ({item.Result.StatusCode}): {item.Result.Error ?? "Invalid acknowledgement."}");
            }
        }

        async Task<HiveBatchUpdateMessage> ReceiveAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var message = await HiveControlProtocol.ReadAsync(stream, HiveControlJson.Default.HiveBatchUpdateMessage, timeout.Token);
            await stream.WriteAsync(new byte[] { 1 }, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            return message;
        }
    }
}
