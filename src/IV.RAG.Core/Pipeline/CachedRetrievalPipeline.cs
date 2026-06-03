using Microsoft.Extensions.Logging;

namespace IV.RAG;

/// <summary>
/// Decorator that adds semantic query caching to any <see cref="IRetrievalPipeline"/>.
/// Embeds the query once, checks <see cref="IQueryCache"/> by cosine similarity,
/// and falls through to the inner pipeline on a miss.
/// Register via <c>AddCachedRetrieval()</c> after adding a cache implementation.
/// </summary>
public sealed class CachedRetrievalPipeline : IRetrievalPipeline
{
    private readonly IRetrievalPipeline _inner;
    private readonly IEmbedder _embedder;
    private readonly IQueryCache _cache;
    private readonly ILogger<CachedRetrievalPipeline>? _logger;

    /// <summary>Initializes a new instance wrapping <paramref name="inner"/> with semantic query caching.</summary>
    public CachedRetrievalPipeline(
        IRetrievalPipeline inner,
        IEmbedder embedder,
        IQueryCache cache,
        ILogger<CachedRetrievalPipeline>? logger = null)
    {
        _inner = inner;
        _embedder = embedder;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> QueryAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();
        var embedding = await _embedder.EmbedAsync(query, cancellationToken);

        var cached = await _cache.GetAsync(embedding, opts, cancellationToken);
        if (cached is not null)
        {
            _logger?.LogDebug("Cache hit for query \"{Query}\".", query);
            return cached;
        }

        _logger?.LogDebug("Cache miss for query \"{Query}\".", query);
        var results = await _inner.QueryAsync(query, opts, cancellationToken);
        if (results.Count > 0)
            await _cache.SetAsync(embedding, opts, results, cancellationToken);
        return results;
    }
}
