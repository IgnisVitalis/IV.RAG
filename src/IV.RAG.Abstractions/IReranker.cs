namespace IV.RAG;

/// <summary>
/// Reranks retrieval candidates using a cross-encoder or similar relevance model.
/// </summary>
/// <remarks>
/// A reranker scores each candidate by examining the query and the chunk text together,
/// producing a more accurate relevance signal than embedding-based similarity alone.
/// Because this requires a model inference call per candidate, it is typically applied
/// to a small shortlist (e.g., top 20) rather than the full corpus.
/// Register an implementation to enable the reranking step in <see cref="HybridRetrievalPipeline"/>.
/// </remarks>
public interface IReranker
{
    /// <summary>
    /// Scores each candidate against <paramref name="query"/> and returns the top
    /// <paramref name="topK"/> results ordered by descending relevance.
    /// Input order does not affect output; all candidates are re-scored.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        int topK,
        CancellationToken cancellationToken = default);
}
