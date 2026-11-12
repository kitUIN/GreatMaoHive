using System.Text.Json.Serialization;

namespace GreatMaoNet.Contracts;

// Keep the compact wire format separate from HTTP responses and persisted JSON.
// Missing optional fields deserialize to null in both current and legacy peers.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WireMessage))]
public partial class WireJson : JsonSerializerContext;
