using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GreatMaoNet.Updates;

public static class UpdateRegistration
{
    public static void AddReleaseUpdates(this IServiceCollection services, IConfiguration configuration, string application, bool windowsService)
    {
        var options = new UpdateOptions();
        configuration.GetSection("Updates").Bind(options);
        options.Validate();
        options.DownloadDirectory = Path.GetFullPath(options.DownloadDirectory, AppContext.BaseDirectory);
        services.AddSingleton(options);
        services.AddHostedService(provider => new UpdateService(options, application, windowsService,
            provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetRequiredService<ILogger<UpdateService>>()));
    }

    public static async Task<bool> HandleCommandAsync(string[] args, string application)
    {
        if (args.Contains("--version")) { Console.WriteLine(UpdateOptions.CurrentVersion); return true; }
        bool download = args.Contains("--download-update");
        if (!download && !args.Contains("--check-update")) return false;
        string[] configurationArgs = args.Where(x => x is not ("--download-update" or "--check-update")).ToArray();
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true).AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables().AddCommandLine(configurationArgs).Build();
        var options = new UpdateOptions();
        configuration.GetSection("Updates").Bind(options);
        options.Validate();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var client = new ReleaseClient(http, options, application, UpdateOptions.Runtime);
        var update = await client.CheckAsync(UpdateOptions.CurrentVersion, timeout.Token);
        if (update is null) Console.WriteLine($"{application} {UpdateOptions.CurrentVersion}: no newer stable release.");
        else
        {
            Console.WriteLine($"{application}: {UpdateOptions.CurrentVersion} -> {update.Version}");
            if (download)
            {
                string directory = Path.GetFullPath(options.DownloadDirectory, AppContext.BaseDirectory);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"{update.Version}-{update.Package.Name}");
                await client.DownloadAsync(update, path, timeout.Token);
                Console.WriteLine($"Downloaded and SHA-256 verified: {path}");
            }
        }
        return true;
    }
}

public sealed class UpdateService(UpdateOptions options, string application, bool windowsService,
    IHostApplicationLifetime lifetime, ILogger<UpdateService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        string helper = Path.Combine(AppContext.BaseDirectory, UpdateInstaller.Executable("GreatMaoNetUpdater"));
        if (!File.Exists(helper))
        {
            logger.LogInformation("Release updater unavailable in this development build; use a complete published package.");
            return;
        }
        // Let the application finish startup before making network requests.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var client = new ReleaseClient(http, options, application, UpdateOptions.Runtime);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                if (await CheckAndStageAsync(client, helper, timeout.Token)) return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Release update failed; monitoring continues and will retry later."); }
            await Task.Delay(TimeSpan.FromMinutes(options.CheckIntervalMinutes), stoppingToken);
        }
    }

    private async Task<bool> CheckAndStageAsync(ReleaseClient client, string helper, CancellationToken token)
    {
        var update = await client.CheckAsync(UpdateOptions.CurrentVersion, token);
        if (update is null) return false;
        logger.LogInformation("Release {Version} available for {Application}.", update.Version, application);
        if (!options.AutoDownload) return false;
        string state = options.DownloadDirectory;
        Directory.CreateDirectory(state);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var updateLock = new FileStream(Path.Combine(state, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(Path.Combine(state, $"failed-{update.Version}.txt"))) return false;
        // An interrupted handoff needs operator inspection, not another overwrite of its recovery state.
        if (File.Exists(Path.Combine(state, "pending.json")))
            throw new IOException("A pending update exists; inspect updater.log and pending.json before retrying.");
        string archive = Path.Combine(state, $"{update.Version}-{update.Package.Name}");
        // Re-verify cached packages on every use; never trust an existing archive just because it exists.
        await client.DownloadAsync(update, archive, token);
        logger.LogInformation("Release downloaded and verified: {Path}", archive);
        if (!options.AutoApply) return false;
        string mode = windowsService ? "windows-service" : Environment.GetEnvironmentVariable("INVOCATION_ID") is not null ? "systemd" : "console";
        if (mode == "systemd" && Environment.GetEnvironmentVariable("GREATMAO_SYSTEMD_UPDATER") != "1")
            throw new InvalidOperationException("Install the updated systemd unit with ExecStopPost before enabling AutoApply.");
        string stage = Path.Combine(state, $"stage-{update.Version}-{Guid.NewGuid():N}");
        string payload = Path.Combine(stage, "payload");
        string[] files = UpdateInstaller.Extract(archive, payload, application);
        var versionStart = new ProcessStartInfo(Path.Combine(payload, UpdateInstaller.Executable(application)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = payload };
        versionStart.ArgumentList.Add("--version");
        using (var versionProcess = Process.Start(versionStart) ?? throw new IOException("Could not validate staged binary."))
        {
            var output = versionProcess.StandardOutput.ReadToEndAsync(token);
            var error = versionProcess.StandardError.ReadToEndAsync(token);
            using var versionTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            versionTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await versionProcess.WaitForExitAsync(versionTimeout.Token); }
            catch { if (!versionProcess.HasExited) versionProcess.Kill(entireProcessTree: true); throw; }
            await error;
            if (versionProcess.ExitCode != 0 || (await output).Trim() != update.Version)
                throw new InvalidDataException("Staged binary version does not match the release.");
        }
        if (OperatingSystem.IsWindows() && windowsService) WindowsServiceAccess.EnsureCanStart(application);
        // Check directory write access before asking the running service to stop.
        string probe = Path.Combine(AppContext.BaseDirectory, $".update-probe-{Guid.NewGuid():N}");
        using (File.Create(probe)) { }
        File.Delete(probe);
        using var parent = Process.GetCurrentProcess();
        var plan = new UpdatePlan
        {
            Application = application, Version = update.Version, PayloadDirectory = payload, Files = files,
            ParentId = parent.Id, ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks, Mode = mode,
            WorkingDirectory = Environment.CurrentDirectory, Arguments = Environment.GetCommandLineArgs().Skip(1).ToArray()
        };
        UpdateInstaller.WritePlan(state, plan);
        try
        {
            if (mode != "systemd")
            {
                // The helper runs from a copy so Windows can replace the installed updater too.
                string runner = Path.Combine(stage, UpdateInstaller.Executable("GreatMaoNetUpdater"));
                File.Copy(helper, runner);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(runner, File.GetUnixFileMode(helper));
                var start = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
                start.ArgumentList.Add(AppContext.BaseDirectory);
                start.ArgumentList.Add(state);
                using var process = Process.Start(start) ?? throw new IOException("Could not launch update helper.");
                // Helper signals readiness before shutdown, so a failed launch cannot stop monitoring.
                string ready = Path.Combine(stage, "ready");
                for (int attempt = 0; !File.Exists(ready); attempt++)
                {
                    if (process.HasExited || attempt >= 100) throw new IOException("Update helper did not become ready.");
                    await Task.Delay(100, token);
                }
            }
        }
        catch { File.Delete(Path.Combine(state, "pending.json")); throw; }
        logger.LogInformation("Applying release {Version}; the application will stop gracefully and restart.", update.Version);
        lifetime.StopApplication();
        return true;
    }
}
