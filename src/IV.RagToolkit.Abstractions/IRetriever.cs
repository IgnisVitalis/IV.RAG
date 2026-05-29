namespace IV.RagToolkit;

/// <summary>Performs similarity search over stored <see cref="Chunk"/> records.</summary>
public interface IRetriever
{
    /// <summary>
    /// Returns chunks most similar to <paramref name="embedding"/>, ordered by descending score.
    /// Results are filtered by <see cref="RetrievalOptions.MinScore"/> and capped at <see cref="RetrievalOptions.TopK"/>.
    /// </summary>
    Task<IReadOnlyList<SearchResult>> RetrieveAsync(float[] embedding, RetrievalOptions options, CancellationToken cancellationToken = default);
}
