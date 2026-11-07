using System.Text.Json.Serialization;

namespace GreatMaoNet.Updates;

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public ReleaseAsset[] Assets { get; set; } = [];
}

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed record AvailableUpdate(string Version, ReleaseAsset Package, ReleaseAsset Checksum);

public sealed class UpdatePlan
{
    public string Application { get; set; } = "";
    public string Version { get; set; } = "";
    public string PayloadDirectory { get; set; } = "";
    public string[] Files { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public int ParentId { get; set; }
    public long ParentStartTicks { get; set; }
    public string ParentIdentity { get; set; } = "";
    public string Mode { get; set; } = "console";
}

[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(UpdatePlan))]
public partial class UpdateJson : JsonSerializerContext;
