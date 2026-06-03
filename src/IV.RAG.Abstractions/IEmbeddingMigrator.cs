namespace IV.RAG;

/// <summary>Re-embeds all chunks that were produced by a different embedding model than the current one.</summary>
public interface IEmbeddingMigrator
{
    /// <summary>Returns <c>true</c> if any chunks need re-embedding with the current model.</summary>
    Task<bool> IsNeededAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-embeds all outdated chunks and updates the vector store in place.
    /// Up to <paramref name="maxConcurrency"/> embed calls are issued in parallel.
    /// Reports progress via <paramref name="progress"/> after each chunk is processed.
    /// </summary>
    Task MigrateAsync(
        IProgress<EmbeddingMigrationProgress>? progress = null,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default);
}
