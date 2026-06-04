using System.Text.Json.Serialization;

namespace IV.RAG;

/// <summary>Wire representation of a <see cref="Chunk"/>.</summary>
/// <param name="Id">The chunk identifier.</param>
/// <param name="Text">The chunk text.</param>
/// <param name="ChunkIndex">Zero-based position of the chunk within its document, if known.</param>
/// <param name="Origin">The chunk's document provenance.</param>
/// <param name="Metadata">Optional chunk metadata.</param>
public sealed record ChunkDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("chunkIndex")] int? ChunkIndex,
    [property: JsonPropertyName("origin")] OriginDto Origin,
    [property: JsonPropertyName("metadata")] Metadata? Metadata);
