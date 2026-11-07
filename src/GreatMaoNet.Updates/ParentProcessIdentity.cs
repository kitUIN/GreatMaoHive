using System.Diagnostics;
using System.Globalization;

namespace GreatMaoNet.Updates;

public static class ParentProcessIdentity
{
    public static string Capture(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            // Process.StartTime on Unix converts boot-relative ticks to wall time. That
            // conversion can differ between runtimes/processes and after clock corrections.
            return FromLinuxProc(File.ReadAllText($"/proc/{process.Id}/stat"),
                File.ReadAllText("/proc/sys/kernel/random/boot_id"));
        }
        if (OperatingSystem.IsWindows())
            return "windows:" + process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        throw new PlatformNotSupportedException("Update process identity supports Windows and Linux only.");
    }

    public static bool Matches(Process process, UpdatePlan plan)
    {
        if (plan.ParentIdentity.Length > 0) return string.Equals(Capture(process), plan.ParentIdentity, StringComparison.Ordinal);
        // Retain compatibility with old Windows update plans; never relax the Unix check
        // with a wall-clock tolerance that could accidentally accept a reused PID.
        return OperatingSystem.IsWindows() && process.StartTime.ToUniversalTime().Ticks == plan.ParentStartTicks;
    }

    public static string FromLinuxProc(string stat, string bootId)
    {
        // comm (field 2) can contain spaces and parentheses. Fields after its final ')'
        // start at field 3; the kernel starttime is field 22, thus index 19 here.
        int close = stat.LastIndexOf(')');
        if (close < 0 || !Guid.TryParseExact(bootId.Trim(), "D", out var boot))
            throw new InvalidDataException("Invalid Linux process identity.");
        string[] fields = stat[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            throw new InvalidDataException("Invalid Linux process starttime.");
        return $"linux:{boot:D}:{ticks.ToString(CultureInfo.InvariantCulture)}";
    }
}
