namespace IV.RAG;

/// <summary>
/// Optional capability for <see cref="IRetriever"/> implementations that perform vector similarity
/// search. Exposes retrieval from a precomputed query embedding so callers that have already
/// embedded the query (for example a semantic cache probe) can avoid embedding it a second time.
/// </summary>
public interface IVectorRetriever : IRetriever
{
    /// <summary>
    /// Returns chunks most relevant to the precomputed <paramref name="embedding"/>, ordered by
    /// descending score. Behaves identically to <see cref="IRetriever.RetrieveAsync"/> except the
    /// query embedding is supplied directly instead of being computed from a query string.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> RetrieveByVectorAsync(
        float[] embedding,
        RetrievalOptions options,
        CancellationToken cancellationToken = default);
}
