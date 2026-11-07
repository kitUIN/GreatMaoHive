using System.Net;
using System.Net.Sockets;

namespace GreatMaoNet.Contracts;

public static class NodeMetadata
{
    public static bool TryNormalize(string? region, string? ip, out string? normalizedRegion, out string? normalizedIp)
    {
        normalizedRegion = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
        normalizedIp = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
        if (normalizedRegion is { Length: > 256 }) return false;
        if (normalizedIp is null) return true;
        // Accept bare IPv4/IPv6 literals, never hostnames, ports, scopes or legacy IPv4 shorthand.
        if (normalizedIp.Length > 45 || normalizedIp.Contains('%') || normalizedIp.Contains('[') ||
            !IPAddress.TryParse(normalizedIp, out var address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork && normalizedIp != address.ToString()) return false;
        normalizedIp = address.ToString();
        return true;
    }
}
