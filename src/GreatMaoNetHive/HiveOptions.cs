using System.Security.Cryptography;
using System.Text;

namespace GreatMaoNetHive;

public sealed class HiveOptions
{
    public string DatabasePath { get; set; } = "data/hive.db";
    public string ReadApiKey { get; set; } = "";
    public string AdminApiKey { get; set; } = "";
    public string? ControlPipeName { get; set; }
    public bool AllowInsecureTransport { get; set; }
    public int OnlineHistoryRetentionDays { get; set; } = 90;
    public int UpgradeGraceSeconds { get; set; } = 300;
    public BeeCredential[] Bees { get; set; } = [];

    public void Validate()
    {
        if (UpgradeGraceSeconds is < 30 or > 3600)
            throw new InvalidOperationException("Hive:UpgradeGraceSeconds must be 30..3600.");
        if (OnlineHistoryRetentionDays is < 1 or > 730)
            throw new InvalidOperationException("Hive:OnlineHistoryRetentionDays must be 1..730.");
        if (ReadApiKey.Length < 32 || Bees.Length == 0 || Bees.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 128 || x.Token.Length < 32))
            throw new InvalidOperationException("Configure Hive:ReadApiKey and Hive:Bees; credentials require at least 32 characters.");
        if (Bees.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Bees.Length ||
            Bees.Select(x => x.Token).Append(ReadApiKey).Distinct(StringComparer.Ordinal).Count() != Bees.Length + 1)
            throw new InvalidOperationException("Bee IDs and all credentials must be unique.");
        if (AdminApiKey.Length != 0 && (AdminApiKey.Length < 32 ||
            Bees.Select(x => x.Token).Append(ReadApiKey).Contains(AdminApiKey, StringComparer.Ordinal)))
            throw new InvalidOperationException("Hive:AdminApiKey must have at least 32 characters and differ from all other credentials.");
        foreach (var bee in Bees) bee.Quota.Validate();
    }

    public static bool Matches(string? authorization, string secret)
    {
        if (string.IsNullOrEmpty(secret) || authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])),
            SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }
}
public sealed class BeeCredential
{
    public string Id { get; set; } = "";
    public string Token { get; set; } = "";
    public TrafficQuotaOptions Quota { get; set; } = new();
}
