namespace IV.RAG;

/// <summary>A retrieved <see cref="Chunk"/> together with its relevance score.</summary>
/// <param name="Chunk">The matched chunk.</param>
/// <param name="Score">
/// Relevance score. Higher means more relevant. Exact semantics depend on the retriever:
/// cosine similarity in [-1, 1] for vector search, RRF fusion score for hybrid search,
/// or a model-specific score after reranking.
/// </param>
public sealed record SearchResult(Chunk Chunk, float Score);
