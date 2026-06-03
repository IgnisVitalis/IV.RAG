namespace IV.RAG;

/// <summary>Configuration for <see cref="IQueryCache"/> implementations.</summary>
public sealed class QueryCacheOptions
{
    /// <summary>
    /// Minimum cosine similarity between an incoming query embedding and a cached query embedding
    /// required to return the cached result. Must be in [0, 1]. Defaults to 0.95.
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.95f;

    /// <summary>Time-to-live for each cache entry. Defaults to 1 hour.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum number of entries retained by the in-memory implementation.
    /// Oldest entries are evicted first when the limit is reached.
    /// Ignored by the Postgres implementation.
    /// Defaults to 1000.
    /// </summary>
    public int MaxEntries { get; set; } = 1000;
}
