using System.IO.Compression;
using System.Text.Json;

namespace GreatMaoNet.Updates;

public static class UpdateInstaller
{
    public static string Executable(string application) => application + (OperatingSystem.IsWindows() ? ".exe" : "");

    public static void ValidateApplication(string application)
    {
        if (application is not ("GreatMaoNetHive" or "GreatMaoNetBee")) throw new InvalidDataException("Unknown application.");
    }

    // Published AOT bundles are flat. Never let an archive replace configuration or user data.
    public static bool IsBinary(string name, string application) =>
        name.Length > 0 && name == Path.GetFileName(name) && !name.Contains('/') && !name.Contains('\\') && !name.Contains(':') &&
        !name.StartsWith('.') && !name.EndsWith('.') && !name.EndsWith(' ') &&
        (name == Executable(application) || name == Executable("GreatMaoNetUpdater") ||
         name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".so", StringComparison.Ordinal) ||
         name.EndsWith(".deps.json", StringComparison.Ordinal) || name.EndsWith(".runtimeconfig.json", StringComparison.Ordinal));

    public static string[] Extract(string archive, string destination, string application)
    {
        ValidateApplication(application);
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            // Validate all paths, even files that will not be installed.
            if (entry.FullName != Path.GetFileName(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.Contains(':') ||
                entry.FullName.StartsWith('.') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Unsafe release archive entry.");
            if (!names.Add(entry.FullName)) throw new InvalidDataException("Duplicate archive entry.");
            total = checked(total + entry.Length);
            if (total > ReleaseClient.MaxPackageBytes * 2 || names.Count > 200) throw new InvalidDataException("Release archive is too large.");
            if (!IsBinary(entry.FullName, application)) continue;
            string path = Path.Combine(destination, entry.FullName);
            entry.ExtractToFile(path);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        string[] files = names.Where(x => IsBinary(x, application)).ToArray();
        if (!files.Contains(Executable(application)) || !files.Contains(Executable("GreatMaoNetUpdater")))
            throw new InvalidDataException("Release archive is missing application/updater binaries.");
        return files;
    }

    public static void WritePlan(string stateDirectory, UpdatePlan plan)
    {
        string path = Path.Combine(stateDirectory, "pending.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(plan, UpdateJson.Default.UpdatePlan));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static UpdatePlan ReadPlan(string stateDirectory)
    {
        var plan = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(stateDirectory, "pending.json")), UpdateJson.Default.UpdatePlan)
            ?? throw new InvalidDataException("Empty update plan.");
        ValidateApplication(plan.Application);
        UpdateOptions.ParseVersion(plan.Version);
        string payload = Path.GetFullPath(plan.PayloadDirectory);
        string root = Path.GetFullPath(stateDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!payload.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            plan.Files.Length == 0 || plan.Files.Any(x => !IsBinary(x, plan.Application)) ||
            plan.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Files.Length ||
            !plan.Files.Contains(Executable(plan.Application)) || !plan.Files.Contains(Executable("GreatMaoNetUpdater")) ||
            plan.Mode is not ("console" or "windows-service" or "systemd"))
            throw new InvalidDataException("Invalid update plan.");
        return plan;
    }

    public static InstalledUpdate Install(string installDirectory, UpdatePlan plan)
    {
        ValidateApplication(plan.Application);
        if (plan.Files.Any(x => !IsBinary(x, plan.Application))) throw new InvalidDataException("Invalid binary file name.");
        string backup = Path.Combine(installDirectory, ".update-backup", $"{plan.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backup);
        var transaction = new InstalledUpdate(installDirectory, backup);
        try
        {
            foreach (string name in plan.Files)
            {
                string target = Path.Combine(installDirectory, name);
                string source = Path.Combine(plan.PayloadDirectory, name);
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
                    File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Update binaries must not be symbolic links.");
                string temporary = Path.Combine(installDirectory, $".update-{Guid.NewGuid():N}");
                try
                {
                    File.Copy(source, temporary);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(source));
                    bool existed = File.Exists(target);
                    if (existed) File.Move(target, Path.Combine(backup, name));
                    transaction.Changed.Add((name, existed));
                    File.Move(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return transaction;
        }
        catch { transaction.Rollback(); throw; }
    }
}

public sealed class InstalledUpdate(string installDirectory, string backupDirectory)
{
    internal List<(string Name, bool Existed)> Changed { get; } = [];
    public string BackupDirectory => backupDirectory;

    public void Rollback()
    {
        foreach (var (name, existed) in Changed.AsEnumerable().Reverse())
        {
            string target = Path.Combine(installDirectory, name);
            if (File.Exists(target)) File.Delete(target);
            if (existed) File.Move(Path.Combine(backupDirectory, name), target);
        }
        Changed.Clear();
    }
}
