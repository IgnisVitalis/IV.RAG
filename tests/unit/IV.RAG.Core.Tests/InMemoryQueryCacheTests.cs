using FluentAssertions;
using Microsoft.Extensions.Options;

namespace IV.RAG.Tests;

public class InMemoryQueryCacheTests
{
    private static readonly Document.Origin Origin1 =
        new(new Guid("a0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    private static readonly Document.Origin Origin2 =
        new(new Guid("b0000000-0000-0000-0000-000000000002"), "Test", "doc-2");

    private static readonly RetrievalOptions DefaultOptions = new();

    private static InMemoryQueryCache Create(QueryCacheOptions? opts = null) =>
        new(Options.Create(opts ?? new QueryCacheOptions { SimilarityThreshold = 0.95f, Ttl = TimeSpan.FromHours(1) }));

    private static float[] Vec(float x, float y, float z) => [x, y, z];

    private static IReadOnlyList<SearchResult> Results(Document.Origin origin) =>
        [new SearchResult(new Chunk { Id = "c1", Text = "t", Origin = origin }, 0.9f)];

    // ─── cache miss / hit ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_EmptyCache_ReturnsNull()
    {
        var cache = Create();
        var result = await cache.GetAsync(Vec(1, 0, 0), DefaultOptions);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_IdenticalEmbedding_ReturnsHit()
    {
        var cache = Create();
        var stored = Results(Origin1);
        await cache.SetAsync(Vec(1, 0, 0), DefaultOptions, stored);

        var result = await cache.GetAsync(Vec(1, 0, 0), DefaultOptions);

        result.Should().BeEquivalentTo(stored);
    }

    [Fact]
    public async Task GetAsync_SimilarEmbedding_AboveThreshold_ReturnsHit()
    {
        var cache = Create(new QueryCacheOptions { SimilarityThreshold = 0.85f, Ttl = TimeSpan.FromHours(1) });
        var stored = Results(Origin1);
        // [1, 0, 0] vs [0.9, 0.436, 0] → cosine ≈ 0.9, above threshold 0.85
        await cache.SetAsync(Vec(1, 0, 0), DefaultOptions, stored);

        var result = await cache.GetAsync(Vec(0.9f, 0.436f, 0f), DefaultOptions);

        result.Should().BeEquivalentTo(stored);
    }

    [Fact]
    public async Task GetAsync_DissimilarEmbedding_BelowThreshold_ReturnsNull()
    {
        var cache = Create(new QueryCacheOptions { SimilarityThreshold = 0.95f, Ttl = TimeSpan.FromHours(1) });
        await cache.SetAsync(Vec(1, 0, 0), DefaultOptions, Results(Origin1));

        // [1, 0, 0] vs [0, 1, 0] → cosine = 0
        var result = await cache.GetAsync(Vec(0, 1, 0), DefaultOptions);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_DifferentOptions_ReturnsNull()
    {
        var cache = Create();
        await cache.SetAsync(Vec(1, 0, 0), new RetrievalOptions { TopK = 5 }, Results(Origin1));

        var result = await cache.GetAsync(Vec(1, 0, 0), new RetrievalOptions { TopK = 10 });

        result.Should().BeNull();
    }

    // ─── TTL ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ExpiredEntry_ReturnsNull()
    {
        var cache = Create(new QueryCacheOptions { SimilarityThreshold = 0.95f, Ttl = TimeSpan.FromMilliseconds(1) });
        await cache.SetAsync(Vec(1, 0, 0), DefaultOptions, Results(Origin1));
        await Task.Delay(10);

        var result = await cache.GetAsync(Vec(1, 0, 0), DefaultOptions);

        result.Should().BeNull();
    }

    // ─── invalidation ────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidateByDocumentAsync_RemovesMatchingEntries()
    {
        var cache = Create();
        await cache.SetAsync(Vec(1, 0, 0), DefaultOptions, Results(Origin1));
        await cache.SetAsync(Vec(0, 1, 0), DefaultOptions, Results(Origin2));

        await cache.InvalidateByDocumentAsync(Origin1);

        (await cache.GetAsync(Vec(1, 0, 0), DefaultOptions)).Should().BeNull();
        (await cache.GetAsync(Vec(0, 1, 0), DefaultOptions)).Should().NotBeNull();
    }

    [Fact]
    public async Task InvalidateByDocumentAsync_UnknownOrigin_LeavesEntriesUntouched()
    {
        var cache = Create();
        var stored = Results(Origin1);
        await cache.SetAsync(Vec(1, 0, 0), DefaultOptions, stored);

        await cache.InvalidateByDocumentAsync(Origin2);

        (await cache.GetAsync(Vec(1, 0, 0), DefaultOptions)).Should().BeEquivalentTo(stored);
    }

    // ─── MaxEntries eviction ─────────────────────────────────────────────────

    [Fact]
    public async Task SetAsync_ExceedsMaxEntries_EvictsOldestEntry()
    {
        // Use high threshold so only exact-match vectors return hits
        var cache = Create(new QueryCacheOptions { SimilarityThreshold = 0.99f, Ttl = TimeSpan.FromHours(1), MaxEntries = 2 });
        await cache.SetAsync(Vec(1, 0, 0), DefaultOptions, Results(Origin1));
        await cache.SetAsync(Vec(0, 1, 0), DefaultOptions, Results(Origin1));
        // Adding a third entry should evict the first [1, 0, 0]
        await cache.SetAsync(Vec(0, 0, 1), DefaultOptions, Results(Origin1));

        (await cache.GetAsync(Vec(1, 0, 0), DefaultOptions)).Should().BeNull();
        (await cache.GetAsync(Vec(0, 1, 0), DefaultOptions)).Should().NotBeNull();
        (await cache.GetAsync(Vec(0, 0, 1), DefaultOptions)).Should().NotBeNull();
    }
}
