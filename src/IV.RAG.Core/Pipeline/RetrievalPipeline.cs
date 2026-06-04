using Microsoft.Extensions.Logging;

namespace IV.RAG;

/// <summary>
/// Local retrieval pipeline: chunk → embed → store (ingest), embed → retrieve (query).
/// </summary>
public sealed class RetrievalPipeline : IIngestionPipeline, IRetrievalPipeline, IVectorQueryPipeline
{
    private readonly IChunker _chunker;
    private readonly IEmbedder _embedder;
    private readonly IVectorStore _vectorStore;
    private readonly IRetriever _retriever;
    private readonly IQueryCache? _queryCache;
    private readonly ILogger<RetrievalPipeline> _logger;

    /// <summary>Initializes a new instance with all required retrieval components.</summary>
    public RetrievalPipeline(
        IChunker chunker,
        IEmbedder embedder,
        IVectorStore vectorStore,
        IRetriever retriever,
        ILogger<RetrievalPipeline> logger,
        IQueryCache? queryCache = null)
    {
        _chunker = chunker;
        _embedder = embedder;
        _vectorStore = vectorStore;
        _retriever = retriever;
        _logger = logger;
        _queryCache = queryCache;
    }

    /// <inheritdoc/>
    public async Task IngestAsync(Document document, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Ingesting document of type {DocumentType}.", document.GetType().Name);

        var rawChunks = new List<Chunk>();
        await foreach (var chunk in _chunker.ChunkAsync(document, cancellationToken))
            rawChunks.Add(chunk);

        var embeddings = await _embedder.EmbedAsync(rawChunks.Select(c => c.Text).ToList(), cancellationToken);

        var chunks = new List<Chunk>(rawChunks.Count);
        for (var i = 0; i < rawChunks.Count; i++)
            chunks.Add(rawChunks[i] with { Id = Guid.NewGuid().ToString(), ChunkIndex = i, Embedding = embeddings[i] });

        await _vectorStore.SetAsync(document.Source, chunks, cancellationToken);
        _logger.LogDebug("Ingested {Count} chunks.", chunks.Count);

        if (_queryCache is not null)
            await _queryCache.InvalidateByDocumentAsync(document.Source, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> QueryAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Querying: \"{Query}\".", query);

        var results = await _retriever.RetrieveAsync(query, options ?? new RetrievalOptions(), cancellationToken);

        _logger.LogDebug("Retrieved {Count} results.", results.Count);
        return results;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> QueryByVectorAsync(
        float[] embedding,
        string query,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Querying by precomputed vector: \"{Query}\".", query);

        // Reuse the caller's embedding when the retriever supports it; otherwise fall back to the
        // string overload (which embeds internally).
        var results = _retriever is IVectorRetriever vectorRetriever
            ? await vectorRetriever.RetrieveByVectorAsync(embedding, options, cancellationToken)
            : await _retriever.RetrieveAsync(query, options, cancellationToken);

        _logger.LogDebug("Retrieved {Count} results.", results.Count);
        return results;
    }
}
