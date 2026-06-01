using Microsoft.Extensions.Logging;

namespace IV.RAG;

/// <summary>
/// Local retrieval pipeline: chunk → embed → store (ingest), embed → retrieve (query).
/// </summary>
public sealed class RetrievalPipeline : IIngestionPipeline, IRetrievalPipeline
{
    private readonly IChunker _chunker;
    private readonly IEmbedder _embedder;
    private readonly IVectorStore _vectorStore;
    private readonly IRetriever _retriever;
    private readonly ILogger<RetrievalPipeline> _logger;

    /// <summary>Initializes a new instance with all required retrieval components.</summary>
    public RetrievalPipeline(
        IChunker chunker,
        IEmbedder embedder,
        IVectorStore vectorStore,
        IRetriever retriever,
        ILogger<RetrievalPipeline> logger)
    {
        _chunker = chunker;
        _embedder = embedder;
        _vectorStore = vectorStore;
        _retriever = retriever;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task IngestAsync(Document document, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Ingesting document of type {DocumentType}.", document.GetType().Name);

        var chunks = new List<Chunk>();
        var chunkIndex = 0;
        await foreach (var chunk in _chunker.ChunkAsync(document, cancellationToken))
        {
            var embedding = await _embedder.EmbedAsync(chunk.Text, cancellationToken);
            chunks.Add(chunk with { Id = Guid.NewGuid().ToString(), ChunkIndex = chunkIndex++, Embedding = embedding });
        }

        await _vectorStore.SetAsync(document.Source, chunks, cancellationToken);
        _logger.LogDebug("Ingested {Count} chunks.", chunks.Count);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> QueryAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Querying: \"{Query}\".", query);

        var embedding = await _embedder.EmbedAsync(query, cancellationToken);
        var results = await _retriever.RetrieveAsync(embedding, options ?? new RetrievalOptions(), cancellationToken);

        _logger.LogDebug("Retrieved {Count} results.", results.Count);
        return results;
    }
}
