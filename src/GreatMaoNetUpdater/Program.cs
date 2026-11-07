using System.Diagnostics;
using GreatMaoNet.Updates;

if (args.Length == 2 && args[0] == "--process-identity" && int.TryParse(args[1], out int processId) && processId > 0)
{
    using var process = Process.GetProcessById(processId);
    Console.WriteLine(ParentProcessIdentity.Capture(process));
    return 0;
}
if (args.Length != 2) { Console.Error.WriteLine("Usage: GreatMaoNetUpdater <installation-directory> <update-directory>"); return 2; }
string install = Path.GetFullPath(args[0]);
string state = Path.GetFullPath(args[1]);
string pending = Path.Combine(state, "pending.json");
if (!File.Exists(pending)) return 0; // ExecStopPost also runs on ordinary service stops.
UpdatePlan? plan = null;
InstalledUpdate? installed = null;
bool parentStopped = false;
try
{
    plan = UpdateInstaller.ReadPlan(state);
    if (plan.Mode != "systemd")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Process? parent = null;
        try { parent = Process.GetProcessById(plan.ParentId); } catch (ArgumentException) { }
        using (parent)
        {
            if (parent is not null && !ParentProcessIdentity.Matches(parent, plan))
                throw new IOException("Parent PID was reused; refusing update.");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(plan.PayloadDirectory)!, "ready"), "ready");
            if (parent is not null) await parent.WaitForExitAsync(timeout.Token);
        }
    }
    parentStopped = true;
    using var updateLock = new FileStream(Path.Combine(state, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    // A canceled handoff removes pending.json while the helper is waiting.
    if (!File.Exists(pending)) return 0;
    installed = UpdateInstaller.Install(install, plan);
    await RestartAsync(plan, install);
    // The replacement is now running. A bookkeeping failure must not roll back live binaries.
    try
    {
        File.Move(pending, Path.Combine(state, "last-update.json"), overwrite: true);
        Log($"Installed {plan.Version}; backup: {installed.BackupDirectory}");
    }
    catch (Exception bookkeeping) { Console.Error.WriteLine("Update succeeded, but recording completion failed: " + bookkeeping); }
    return 0;
}
catch (Exception exception)
{
    Log(exception.ToString());
    try
    {
        installed?.Rollback();
        if (plan is not null)
        {
            File.WriteAllText(Path.Combine(state, $"failed-{plan.Version}.txt"), exception.ToString());
            if (File.Exists(pending)) File.Move(pending, Path.Combine(state, "failed-update.json"), overwrite: true);
            if (parentStopped) await RestartAsync(plan, install);
        }
    }
    catch (Exception recovery) { Log("Recovery failed: " + recovery); }
    return 1;
}

void Log(string message)
{
    Console.Error.WriteLine(message);
    File.AppendAllText(Path.Combine(state, "updater.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
}

static async Task RestartAsync(UpdatePlan plan, string install)
{
    if (plan.Mode == "systemd") return; // Restart=always restarts the unit after ExecStopPost completes.
    if (plan.Mode == "windows-service")
    {
        // SCM can remain STOP_PENDING briefly after the old process exits.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("start");
            start.ArgumentList.Add(plan.Application);
            using var process = Process.Start(start) ?? throw new IOException("Could not start service controller.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            if (process.ExitCode is 0 or 1056) return; // ERROR_SERVICE_ALREADY_RUNNING
            await Task.Delay(1000);
        }
        throw new IOException("Could not restart Windows service; grant its account permission to start this service.");
    }
    var info = new ProcessStartInfo(Path.Combine(install, UpdateInstaller.Executable(plan.Application)))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = plan.WorkingDirectory };
    foreach (string argument in plan.Arguments) info.ArgumentList.Add(argument);
    using var child = Process.Start(info) ?? throw new IOException("Could not restart application.");
    await Task.Delay(3000);
    if (child.HasExited) throw new IOException($"Updated application exited during startup ({child.ExitCode}).");
}
