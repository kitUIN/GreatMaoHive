namespace GreatMaoNetBee;

public sealed class BeeOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = Environment.MachineName;
    public string HiveUrl { get; set; } = "wss://localhost:7443/agent";
    public string Token { get; set; } = "";
    public string DatabasePath { get; set; } = "data/bee.db";
    public string[] Interfaces { get; set; } = [];
    public bool AllowInsecureTransport { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || Token.Length < 32)
            throw new InvalidOperationException("Bee:Id is required; Bee:Token must contain at least 32 characters.");
        if (!Uri.TryCreate(HiveUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "wss" || (uri.Scheme == "ws" && AllowInsecureTransport)))
            throw new InvalidOperationException("Bee:HiveUrl must use wss (ws requires AllowInsecureTransport).");
    }
}
