namespace IV.RAG;

/// <summary>Progress snapshot reported during <see cref="IEmbeddingMigrator.MigrateAsync"/>.</summary>
public sealed record EmbeddingMigrationProgress(
    /// <summary>Total number of chunks that require re-embedding.</summary>
    int Total,
    /// <summary>Number of chunks successfully re-embedded so far.</summary>
    int Processed,
    /// <summary>Origin of the document whose chunk was most recently processed.</summary>
    Document.Origin CurrentOrigin);
