using System.Text.Json.Serialization;

namespace IV.RAG.Http;

internal sealed record EmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string[] Input);
