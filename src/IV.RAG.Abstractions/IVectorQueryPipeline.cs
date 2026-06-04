namespace IV.RAG;

/// <summary>
/// Optional capability for <see cref="IRetrievalPipeline"/> implementations that can retrieve from a
/// precomputed query embedding. Lets a wrapping cache decorator reuse the embedding it computed for
/// the cache lookup instead of forcing the inner pipeline (and its retriever) to embed again.
/// </summary>
public interface IVectorQueryPipeline
{
    /// <summary>
    /// Returns the most relevant chunks for a query whose embedding has already been computed.
    /// The <paramref name="embedding"/> drives vector similarity search; the original
    /// <paramref name="query"/> string is still supplied for components that need it, such as
    /// lexical search and reranking.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> QueryByVectorAsync(
        float[] embedding,
        string query,
        RetrievalOptions options,
        CancellationToken cancellationToken = default);
}
