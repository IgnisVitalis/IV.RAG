namespace IV.RAG;

/// <summary>
/// Semantic query cache: stores retrieval results keyed by query embedding and options.
/// Lookups are similarity-based — a cached result is returned when a stored query embedding
/// is within the configured cosine similarity threshold of the incoming query embedding.
/// </summary>
public interface IQueryCache
{
    /// <summary>
    /// Returns cached results for a semantically similar previous query, or <see langword="null"/> on a miss.
    /// Similarity is measured by cosine distance against <paramref name="queryEmbedding"/>,
    /// combined with exact <paramref name="options"/> equality and a configurable threshold.
    /// </summary>
    Task<IReadOnlyList<SearchResult>?> GetAsync(
        float[] queryEmbedding,
        RetrievalOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Stores retrieval results for future cache lookups.</summary>
    Task SetAsync(
        float[] queryEmbedding,
        RetrievalOptions options,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all cache entries that contain chunks from the specified document.
    /// Called automatically by <see cref="RetrievalPipeline"/> when a document is re-ingested.
    /// </summary>
    Task InvalidateByDocumentAsync(
        Document.Origin origin,
        CancellationToken cancellationToken = default);
}
