using System.Security.Cryptography;
using System.Text;

namespace GreatMaoNetHive;

public sealed class HiveOptions
{
    public string DatabasePath { get; set; } = "data/hive.db";
    public string ReadApiKey { get; set; } = "";
    public bool AllowInsecureTransport { get; set; }
    public int OnlineHistoryRetentionDays { get; set; } = 90;
    public BeeCredential[] Bees { get; set; } = [];

    public void Validate()
    {
        if (OnlineHistoryRetentionDays is < 1 or > 730)
            throw new InvalidOperationException("Hive:OnlineHistoryRetentionDays must be 1..730.");
        if (ReadApiKey.Length < 32 || Bees.Length == 0 || Bees.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 128 || x.Token.Length < 32))
            throw new InvalidOperationException("Configure Hive:ReadApiKey and Hive:Bees; credentials require at least 32 characters.");
        if (Bees.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Bees.Length ||
            Bees.Select(x => x.Token).Append(ReadApiKey).Distinct(StringComparer.Ordinal).Count() != Bees.Length + 1)
            throw new InvalidOperationException("Bee IDs and all credentials must be unique.");
        foreach (var bee in Bees) bee.Quota.Validate();
    }

    public static bool Matches(string? authorization, string secret)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
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
