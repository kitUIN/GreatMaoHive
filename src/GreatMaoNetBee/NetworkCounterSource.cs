using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using GreatMaoNet.Contracts;

namespace GreatMaoNetBee;

public interface ICounterSource
{
    string BootId { get; }
    CounterReading[] Read(double monotonicSeconds);
}

public sealed class NetworkCounterSource(BeeOptions options, ILogger<NetworkCounterSource> logger) : ICounterSource
{
    private sealed record Selected(NetworkInterface Network, InterfaceInfo Info, string Identity);
    private Selected[] selected = [];
    private double nextRefresh;
    public string BootId { get; } = GetBootId();

    public CounterReading[] Read(double monotonicSeconds)
    {
        if (monotonicSeconds >= nextRefresh)
        {
            nextRefresh = monotonicSeconds + 5;
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray();
            NetworkInterface[] matches;
            if (options.Interfaces.Length > 0)
                matches = all.Where(x => options.Interfaces.Contains(x.Id, StringComparer.Ordinal) || options.Interfaces.Contains(x.Name, StringComparer.Ordinal)).ToArray();
            else
            {
                string? id = OperatingSystem.IsWindows() ? WindowsNetwork.DefaultInterface(all) : LinuxDefaultInterface(all);
                matches = all.Where(x => x.Id == id || x.Name == id).Take(1).ToArray();
            }
            selected = matches.Select(nic =>
            {
                long speed = 0;
                try { speed = nic.Speed; } catch (NetworkInformationException) { }
                var info = new InterfaceInfo(nic.Id, nic.Name, speed > 0 ? speed : null);
                int index = nic.Supports(NetworkInterfaceComponent.IPv4) ? nic.GetIPProperties().GetIPv4Properties()!.Index : nic.GetIPProperties().GetIPv6Properties()!.Index;
                return new Selected(nic, info, $"{nic.Id}/{index}/{nic.GetPhysicalAddress()}");
            }).ToArray();
        }

        var result = new List<CounterReading>();
        foreach (var nic in selected)
        {
            try
            {
                var statistics = nic.Network.GetIPStatistics();
                result.Add(new(nic.Info, nic.Identity, statistics.BytesReceived, statistics.BytesSent));
            }
            catch (Exception exception) when (exception is NetworkInformationException or IOException)
            {
                logger.LogWarning("Cannot read interface {Interface}: {Message}", nic.Info.Name, exception.Message);
                nextRefresh = 0;
            }
        }
        return result.ToArray();
    }

    public static string? ParseLinuxDefaultRoute(IEnumerable<string> ipv4, IEnumerable<string> ipv6)
    {
        var routes = new List<(string Name, long Metric)>();
        foreach (var line in ipv4)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 8 && parts[1] == "00000000" && parts[7] == "00000000" &&
                uint.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) &&
                (flags & 1) != 0 && (flags & 0x200) == 0 && long.TryParse(parts[6], out var metric)) routes.Add((parts[0], metric));
        }
        if (routes.Count == 0)
        {
            foreach (var line in ipv6)
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 10 && parts[0] == new string('0', 32) && parts[1] == "00" && parts[3] == "00" &&
                    uint.TryParse(parts[8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) &&
                    (flags & 1) != 0 && (flags & 0x200) == 0 &&
                    long.TryParse(parts[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var metric)) routes.Add((parts[9], metric));
            }
        }
        return routes.OrderBy(x => x.Metric).ThenBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name).FirstOrDefault();
    }

    private static string? LinuxDefaultInterface(NetworkInterface[] interfaces)
    {
        var names = interfaces.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        return ParseLinuxDefaultRoute(
            File.Exists("/proc/net/route") ? File.ReadLines("/proc/net/route").Where(line => names.Contains(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")) : [],
            File.Exists("/proc/net/ipv6_route") ? File.ReadLines("/proc/net/ipv6_route").Where(line => names.Contains(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")) : []);
    }

    private static string GetBootId()
    {
        if (OperatingSystem.IsLinux()) return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        if (OperatingSystem.IsWindows()) return WindowsNetwork.BootId();
        throw new PlatformNotSupportedException("Only Windows and Linux are supported.");
    }
}

internal static partial class WindowsNetwork
{
    // Layouts from netioapi.h: MIB_IPFORWARD_ROW2 (104), MIB_IPINTERFACE_ROW (168).
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct RouteRow
    {
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(40)] public byte PrefixLength;
        [FieldOffset(76)] public uint ValidLifetime;
        [FieldOffset(84)] public uint Metric;
        [FieldOffset(92)] public byte Loopback;
    }
    [StructLayout(LayoutKind.Explicit, Size = 168)]
    private struct InterfaceRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(16)] public uint InterfaceIndex;
        [FieldOffset(148)] public uint Metric;
        [FieldOffset(156)] public byte Connected;
        [FieldOffset(166)] public byte DisableDefaultRoutes;
    }
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct BootInformation { [FieldOffset(0)] public long BootTime; }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpForwardTable2(ushort family, out nint table);
    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(nint table);
    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpInterfaceEntry(ref InterfaceRow row);
    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int informationClass, out BootInformation information, uint length, out uint returnLength);

    public static string BootId()
    {
        if (NtQuerySystemInformation(3, out var information, 48, out _) != 0)
            throw new InvalidOperationException("Cannot determine Windows boot identity.");
        return information.BootTime.ToString(CultureInfo.InvariantCulture);
    }

    public static unsafe string? DefaultInterface(NetworkInterface[] interfaces)
    {
        foreach (ushort family in new ushort[] { 2, 23 })
        {
            if (GetIpForwardTable2(family, out var table) != 0) continue;
            try
            {
                var candidates = new List<(uint Index, ulong Metric)>();
                uint count = *(uint*)table;
                var rows = (RouteRow*)((byte*)table + 8);
                for (int i = 0; i < count; i++)
                {
                    var route = rows[i];
                    if (route.PrefixLength != 0 || route.Loopback != 0 || route.ValidLifetime == 0) continue;
                    var nic = new InterfaceRow { Family = family, InterfaceIndex = route.InterfaceIndex };
                    if (GetIpInterfaceEntry(ref nic) == 0 && nic.Connected != 0 && nic.DisableDefaultRoutes == 0)
                        candidates.Add((route.InterfaceIndex, (ulong)route.Metric + nic.Metric));
                }
                foreach (var candidate in candidates.OrderBy(x => x.Metric).ThenBy(x => x.Index))
                {
                    var match = interfaces.Where(x => x.Supports(family == 2 ? NetworkInterfaceComponent.IPv4 : NetworkInterfaceComponent.IPv6))
                        .FirstOrDefault(x => (family == 2 ? x.GetIPProperties().GetIPv4Properties()?.Index : x.GetIPProperties().GetIPv6Properties()?.Index) == candidate.Index);
                    if (match is not null) return match.Id;
                }
            }
            finally { FreeMibTable(table); }
        }
        return null;
    }
}
