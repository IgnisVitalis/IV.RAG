namespace IV.RAG;

/// <summary>
/// Performs relevance search over stored <see cref="Chunk"/> records.
/// Implementations embed the query internally (vector search) or use keyword matching (lexical search).
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Returns chunks most relevant to <paramref name="query"/>, ordered by descending score.
    /// Results are filtered by <see cref="RetrievalOptions.MinScore"/>, narrowed by
    /// <see cref="RetrievalOptions.MetadataFilter"/> when set, and capped at <see cref="RetrievalOptions.TopK"/>.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> RetrieveAsync(string query, RetrievalOptions options, CancellationToken cancellationToken = default);
}
