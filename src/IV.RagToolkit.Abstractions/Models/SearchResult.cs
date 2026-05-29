namespace IV.RagToolkit;

/// <summary>A retrieved <see cref="Chunk"/> together with its similarity score.</summary>
/// <param name="Chunk">The matched chunk.</param>
/// <param name="Score">Cosine similarity score in the range [-1, 1]. Higher is more similar. 1 = identical, 0 = unrelated, -1 = opposite.</param>
public sealed record SearchResult(Chunk Chunk, float Score);
