using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GreatMaoNet.Updates;

public interface IConsoleStatusProvider
{
    IEnumerable<string> GetStatus();
}

public interface IReleaseCommands
{
    Task CheckAsync(string currentVersion, bool download, TextWriter output, CancellationToken token);
}

public static class ConsoleRegistration
{
    public static void AddConsoleCommands(this IServiceCollection services, IConfiguration configuration,
        string application, bool windowsService)
    {
        bool enabled = configuration.GetValue<bool?>("Console:Enabled") ??
            (Environment.UserInteractive && !Console.IsInputRedirected);
        if (!enabled || windowsService || Environment.GetEnvironmentVariable("INVOCATION_ID") is not null) return;
        services.AddSingleton(provider => new ConsoleCommandProcessor(application,
            provider.GetRequiredService<IConsoleStatusProvider>(), provider.GetRequiredService<IReleaseCommands>(),
            provider.GetRequiredService<IHostApplicationLifetime>(), Console.Out));
        services.AddHostedService(provider => new ConsoleCommandService(Console.In, Console.Out,
            provider.GetRequiredService<ConsoleCommandProcessor>(), provider.GetRequiredService<IHostApplicationLifetime>()));
    }
}

public sealed class ConsoleCommandProcessor(string application, IConsoleStatusProvider status,
    IReleaseCommands updates, IHostApplicationLifetime lifetime, TextWriter output)
{
    public async Task ExecuteAsync(string command, CancellationToken token)
    {
        string normalized = command.Trim().ToLowerInvariant();
        try
        {
            switch (normalized)
            {
                case "": return;
                case "help" or "?":
                    await output.WriteLineAsync("Commands: help | status (connections) | version | check-update | download-update | exit (quit)");
                    await output.WriteLineAsync("check-update only checks; download-update downloads and verifies without installing. exit stops this application gracefully.");
                    break;
                case "status" or "connections":
                    foreach (string line in status.GetStatus()) await output.WriteLineAsync(line);
                    break;
                case "version":
                    await output.WriteLineAsync($"{application} {UpdateOptions.CurrentVersion}");
                    break;
                case "check-update" or "download-update":
                    await output.WriteLineAsync("Checking the latest stable release...");
                    await updates.CheckAsync(UpdateOptions.CurrentVersion, normalized == "download-update", output, token);
                    break;
                case "exit" or "quit":
                    await output.WriteLineAsync($"Stopping {application}...");
                    lifetime.StopApplication();
                    break;
                default:
                    await output.WriteLineAsync("Unknown command. Type help for available commands.");
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await output.WriteLineAsync($"Command failed: {exception.Message}");
        }
    }
}

public sealed class ConsoleCommandService(TextReader input, TextWriter output,
    ConsoleCommandProcessor commands, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        await output.WriteLineAsync("Console commands ready. Type help and press Enter.");
        var lines = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
        // Console.In may implement ReadLineAsync synchronously and ignore cancellation.
        // A background thread keeps shutdown responsive even while the terminal waits for Enter.
        var reader = new Thread(() =>
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested && input.ReadLine() is { } line)
                    lines.Writer.WriteAsync(line, stoppingToken).AsTask().GetAwaiter().GetResult();
                lines.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { lines.Writer.TryComplete(); }
            catch (Exception exception) { lines.Writer.TryComplete(exception); }
        }) { IsBackground = true, Name = "Console commands" };
        reader.Start();
        try
        {
            await foreach (string line in lines.Reader.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested) break;
                await commands.ExecuteAsync(line, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) { await output.WriteLineAsync($"Console input closed: {exception.Message}"); }
        // EOF closes command input only; monitoring continues until Ctrl+C or host shutdown.
    }
}
