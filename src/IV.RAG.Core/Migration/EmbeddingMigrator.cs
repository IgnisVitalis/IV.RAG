using Microsoft.Extensions.Logging;

namespace IV.RAG;

/// <summary>Re-embeds all chunks that were produced by a different embedding model than the current one.</summary>
public sealed class EmbeddingMigrator : IEmbeddingMigrator
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbedder _embedder;
    private readonly ILogger<EmbeddingMigrator>? _logger;

    /// <summary>Initializes a new instance with the provided vector store and embedder.</summary>
    public EmbeddingMigrator(IVectorStore vectorStore, IEmbedder embedder, ILogger<EmbeddingMigrator>? logger = null)
    {
        _vectorStore = vectorStore;
        _embedder = embedder;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> IsNeededAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var _ in _vectorStore.GetOutdatedAsync(cancellationToken))
                return true;
            return false;
        }
        catch (EmbeddingModelMismatchException)
        {
            return true;
        }
    }

    /// <inheritdoc/>
    public async Task MigrateAsync(
        IProgress<EmbeddingMigrationProgress>? progress = null,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
    {
        int total;
        try
        {
            total = await _vectorStore.CountOutdatedAsync(cancellationToken);
        }
        catch (EmbeddingModelMismatchException)
        {
            // EnsureSchemaAsync threw on first use; schema and _currentModelId are now set.
            // Retry — this call goes through the fast path.
            total = await _vectorStore.CountOutdatedAsync(cancellationToken);
        }

        if (total == 0) return;

        _logger?.LogInformation(
            "Starting embedding migration: {Count} chunks to re-embed using {Model}.",
            total, _embedder.ModelInfo);

        var processed = 0;
        var batch = new List<Chunk>(maxConcurrency);

        async Task FlushAsync()
        {
            await Task.WhenAll(batch.Select(async chunk =>
            {
                var newEmbedding = await _embedder.EmbedAsync(chunk.Text, cancellationToken);
                await _vectorStore.UpdateEmbeddingAsync(chunk.Id!, newEmbedding, cancellationToken);
                var done = Interlocked.Increment(ref processed);
                progress?.Report(new EmbeddingMigrationProgress(total, done, chunk.Origin));
            }));
            batch.Clear();
        }

        await foreach (var chunk in _vectorStore.GetOutdatedAsync(cancellationToken))
        {
            batch.Add(chunk);
            if (batch.Count == maxConcurrency)
                await FlushAsync();
        }

        if (batch.Count > 0)
            await FlushAsync();

        _logger?.LogInformation("Embedding migration complete: {Count} chunks re-embedded.", total);
    }
}
