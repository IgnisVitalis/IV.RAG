namespace IV.RAG;

/// <summary>Performs lexical (keyword/BM25) search over stored <see cref="Chunk"/> records.</summary>
public interface ILexicalRetriever
{
    /// <summary>
    /// Returns chunks matching <paramref name="query"/> by keyword relevance, ordered by descending score.
    /// Results are narrowed by <see cref="RetrievalOptions.MetadataFilter"/> when set,
    /// and capped at <see cref="RetrievalOptions.TopK"/>.
    /// <see cref="RetrievalOptions.MinScore"/> is not applied — the full-text match predicate
    /// already ensures only relevant chunks are returned.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> RetrieveAsync(string query, RetrievalOptions options, CancellationToken cancellationToken = default);
}
