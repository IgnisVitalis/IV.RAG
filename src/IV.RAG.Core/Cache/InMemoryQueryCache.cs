using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace IV.RAG;

/// <summary>Thread-safe in-memory implementation of <see cref="IQueryCache"/>.</summary>
public sealed class InMemoryQueryCache : IQueryCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly QueryCacheOptions _options;
    private readonly List<CacheEntry> _entries = [];
    private readonly object _lock = new();

    /// <summary>Initializes a new instance with the provided cache options.</summary>
    public InMemoryQueryCache(IOptions<QueryCacheOptions> options) =>
        _options = options.Value;

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchResult>?> GetAsync(
        float[] queryEmbedding,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        var optionsJson = JsonSerializer.Serialize(options, JsonOptions);
        var now = DateTimeOffset.UtcNow;

        List<CacheEntry> snapshot;
        lock (_lock)
        {
            _entries.RemoveAll(e => e.ExpiresAt <= now);
            snapshot = [.._entries.Where(e => e.OptionsJson == optionsJson)];
        }

        var best = snapshot
            .Select(e => (entry: e, sim: CosineSimilarity(queryEmbedding, e.QueryEmbedding)))
            .Where(x => x.sim >= _options.SimilarityThreshold)
            .OrderByDescending(x => x.sim)
            .Select(x => x.entry)
            .FirstOrDefault();

        if (best is not null)
        {
            // LRU: move the hit to the back so it is the last to be evicted.
            lock (_lock)
            {
                if (_entries.Remove(best))
                    _entries.Add(best);
            }
        }

        return Task.FromResult<IReadOnlyList<SearchResult>?>(best?.Results);
    }

    /// <inheritdoc/>
    public Task SetAsync(
        float[] queryEmbedding,
        RetrievalOptions options,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default)
    {
        var optionsJson = JsonSerializer.Serialize(options, JsonOptions);
        var origins = results.Select(r => r.Chunk.Origin).ToHashSet();
        var entry = new CacheEntry(
            queryEmbedding,
            optionsJson,
            results,
            origins,
            DateTimeOffset.UtcNow.Add(_options.Ttl));

        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            _entries.RemoveAll(e => e.ExpiresAt <= now);
            if (_entries.Count >= _options.MaxEntries)
                _entries.RemoveAt(0); // evict least-recently-used (reads move hits to the back)
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InvalidateByDocumentAsync(
        Document.Origin origin,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
            _entries.RemoveAll(e => e.DocumentOrigins.Contains(origin));
        return Task.CompletedTask;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f, normA = 0f, normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0f || normB == 0f) return 0f;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    private sealed record CacheEntry(
        float[] QueryEmbedding,
        string OptionsJson,
        IReadOnlyList<SearchResult> Results,
        HashSet<Document.Origin> DocumentOrigins,
        DateTimeOffset ExpiresAt);
}
