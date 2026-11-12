using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GreatMaoNet.Updates;

public sealed partial class UpdateOptions
{
    public bool Enabled { get; set; } = true;
    public bool AutoDownload { get; set; } = true;
    public bool AutoApply { get; set; } = true;
    public bool AllowRemoteUpdates { get; set; } = true;
    public string Repository { get; set; } = "kitUIN/GreatMaoHive";
    public int CheckIntervalMinutes { get; set; } = 360;
    public string DownloadDirectory { get; set; } = ".updates";

    public void Validate()
    {
        if (!RepositoryPattern().IsMatch(Repository)) throw new ArgumentException("Updates:Repository must be owner/repository.");
        if (CheckIntervalMinutes is < 1 or > 10080) throw new ArgumentException("Updates:CheckIntervalMinutes must be 1..10080.");
        if (string.IsNullOrWhiteSpace(DownloadDirectory)) throw new ArgumentException("Updates:DownloadDirectory is required.");
        if (AutoApply && !AutoDownload) throw new ArgumentException("Updates:AutoApply requires AutoDownload.");
    }

    public static string CurrentVersion => (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "0.2.0").Split('+')[0];

    public static string Runtime => RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : throw new PlatformNotSupportedException()
        : throw new PlatformNotSupportedException("Release updates support Windows/Linux x64 only.");

    public static Version ParseVersion(string value)
    {
        string normalized = value.StartsWith('v') ? value[1..] : value;
        if (!VersionPattern().IsMatch(normalized) || !Version.TryParse(normalized, out var version))
            throw new InvalidDataException("Release version must be vMAJOR.MINOR.PATCH (stable releases only).");
        return version;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex VersionPattern();
}
