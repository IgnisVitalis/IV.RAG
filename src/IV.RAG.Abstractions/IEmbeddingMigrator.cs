namespace IV.RAG;

/// <summary>Re-embeds all chunks that were produced by a different embedding model than the current one.</summary>
public interface IEmbeddingMigrator
{
    /// <summary>Returns <c>true</c> if any chunks need re-embedding with the current model.</summary>
    Task<bool> IsNeededAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-embeds all outdated chunks and updates the vector store in place.
    /// Outdated chunks are processed in batches of up to <paramref name="batchSize"/>, embedded with
    /// a single batch call per batch where the embedder supports it.
    /// Reports progress via <paramref name="progress"/> after each chunk is processed.
    /// </summary>
    Task MigrateAsync(
        IProgress<EmbeddingMigrationProgress>? progress = null,
        int batchSize = 32,
        CancellationToken cancellationToken = default);
}
