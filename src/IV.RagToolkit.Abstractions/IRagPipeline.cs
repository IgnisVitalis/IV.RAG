namespace IV.RagToolkit;

/// <summary>
/// Orchestrates the full RAG flow.
/// Ingest: chunk → embed → store.
/// Query: embed → retrieve.
/// </summary>
public interface IRagPipeline
{
    /// <summary>Chunks, embeds, and stores <paramref name="document"/>.</summary>
    Task IngestAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>Embeds <paramref name="query"/> and returns the most relevant chunks according to <paramref name="options"/>.</summary>
    Task<IReadOnlyList<SearchResult>> QueryAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default);
}
