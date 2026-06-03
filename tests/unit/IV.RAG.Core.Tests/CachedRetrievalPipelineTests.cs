using FluentAssertions;
using NSubstitute;

namespace IV.RAG.Tests;

public class CachedRetrievalPipelineTests
{
    private readonly IRetrievalPipeline _inner = Substitute.For<IRetrievalPipeline>();
    private readonly IEmbedder _embedder = Substitute.For<IEmbedder>();
    private readonly IQueryCache _cache = Substitute.For<IQueryCache>();
    private static readonly float[] Embedding = [1f, 0f, 0f];

    private CachedRetrievalPipeline Create() =>
        new(_inner, _embedder, _cache, logger: null);

    private static SearchResult Result(string id) =>
        new(new Chunk { Id = id, Text = id, Origin = new Document.Origin(Guid.NewGuid(), "Test", id) }, 0.9f);

    [Fact]
    public async Task QueryAsync_CacheHit_ReturnsFromCache_SkipsInner()
    {
        var cached = new[] { Result("A") };
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Embedding);
        _cache.GetAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(cached);

        var result = await Create().QueryAsync("question");

        result.Should().BeEquivalentTo(cached);
        await _inner.DidNotReceive().QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_CacheMiss_CallsInner_ThenStoresResult()
    {
        var expected = new[] { Result("B") };
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Embedding);
        _cache.GetAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SearchResult>?)null);
        _inner.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await Create().QueryAsync("question");

        result.Should().BeEquivalentTo(expected);
        await _cache.Received(1).SetAsync(Embedding, Arg.Any<RetrievalOptions>(), expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_PassesEmbeddingToCache()
    {
        _embedder.EmbedAsync("question", Arg.Any<CancellationToken>()).Returns(Embedding);
        _cache.GetAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SearchResult>?)null);
        _inner.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await Create().QueryAsync("question");

        await _cache.Received(1).GetAsync(Embedding, Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_NullOptions_UsesDefaultsForCacheKey()
    {
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Embedding);
        _cache.GetAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SearchResult>?)null);
        _inner.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await Create().QueryAsync("q", null);

        await _cache.Received(1).GetAsync(
            Arg.Any<float[]>(),
            Arg.Is<RetrievalOptions>(o => o.TopK == 5 && o.MinScore == 0f),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_EmptyResults_AreNotCached()
    {
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Embedding);
        _cache.GetAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SearchResult>?)null);
        _inner.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await Create().QueryAsync("question");

        await _cache.DidNotReceive().SetAsync(
            Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(),
            Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_CacheMiss_InnerReceivesResolvedOptions()
    {
        var opts = new RetrievalOptions { TopK = 10 };
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Embedding);
        _cache.GetAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SearchResult>?)null);
        _inner.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await Create().QueryAsync("q", opts);

        await _inner.Received(1).QueryAsync("q", opts, Arg.Any<CancellationToken>());
    }
}
