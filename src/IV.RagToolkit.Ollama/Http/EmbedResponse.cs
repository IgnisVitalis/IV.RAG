using System.Text.Json.Serialization;

namespace IV.RagToolkit.Http;

internal sealed record EmbedResponse(
    [property: JsonPropertyName("embeddings")] float[][] Embeddings);
