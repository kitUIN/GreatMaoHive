namespace GreatMaoNet.Updates;

public sealed class ReleaseCommands(UpdateOptions options, string application, HttpClient http) : IReleaseCommands, IDisposable
{
    public async Task CheckAsync(string currentVersion, bool download, TextWriter output, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(download ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
        var client = new ReleaseClient(http, options, application, UpdateOptions.Runtime);
        var update = await client.CheckAsync(currentVersion, timeout.Token, requireAccessibleRelease: true);
        if (update is null)
        {
            await output.WriteLineAsync($"{application} {currentVersion}: no newer stable release.");
            return;
        }
        await output.WriteLineAsync($"{application}: {currentVersion} -> {update.Version}");
        if (!download) return;
        string directory = Path.GetFullPath(options.DownloadDirectory, AppContext.BaseDirectory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Share the automatic updater's lock so manual downloads cannot overlap an installation.
        using var updateLock = new FileStream(Path.Combine(directory, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(Path.Combine(directory, "pending.json")))
            throw new IOException("A pending update exists; inspect updater.log and pending.json before retrying.");
        string path = Path.Combine(directory, $"{update.Version}-{update.Package.Name}");
        await client.DownloadAsync(update, path, timeout.Token);
        await output.WriteLineAsync($"Downloaded and SHA-256 verified: {path}");
        await output.WriteLineAsync("The update has not been installed; the running application is unchanged.");
    }

    public void Dispose() => http.Dispose();
}
