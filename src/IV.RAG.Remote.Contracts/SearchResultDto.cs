using System.Text.Json.Serialization;

namespace IV.RAG;

/// <summary>Wire representation of a <see cref="SearchResult"/>.</summary>
/// <param name="Chunk">The matched chunk.</param>
/// <param name="Score">The relevance score.</param>
public sealed record SearchResultDto(
    [property: JsonPropertyName("chunk")] ChunkDto Chunk,
    [property: JsonPropertyName("score")] float Score);
