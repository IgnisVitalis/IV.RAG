using System.Text.Json.Serialization;

namespace IV.RagToolkit.Http;

internal sealed record EmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input);
