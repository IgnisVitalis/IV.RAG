using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace IV.RAG.Tests;

/// <summary>
/// Verifies the vector-reuse seam: a cached cold query embeds the query exactly once (the cache
/// probe) and feeds that embedding to the retriever instead of re-embedding.
/// </summary>
public class VectorReuseTests
{
    private static readonly float[] Embedding = [1f, 0f, 0f];
    private static readonly Document.Origin Origin = new(Guid.NewGuid(), "Test", "doc");

    private readonly IEmbedder _embedder = Substitute.For<IEmbedder>();
    private readonly IQueryCache _cache = Substitute.For<IQueryCache>();

    public VectorReuseTests()
    {
        _embedder.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(Embedding);
        _cache.GetAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SearchResult>?)null); // miss by default
    }

    private static SearchResult Result(string id) =>
        new(new Chunk { Id = id, Text = id, Origin = Origin }, 0.9f);

    private RetrievalPipeline VectorPipeline(IRetriever retriever) =>
        new(Substitute.For<IChunker>(), _embedder, Substitute.For<IVectorStore>(), retriever, NullLogger<RetrievalPipeline>.Instance);

    [Fact]
    public async Task CachedColdQuery_VectorOnly_EmbedsOnce_AndReusesVectorInRetriever()
    {
        var retriever = Substitute.For<IVectorRetriever>();
        retriever.RetrieveByVectorAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Result("A") });
        var cached = new CachedRetrievalPipeline(VectorPipeline(retriever), _embedder, _cache);

        await cached.QueryAsync("q");

        await _embedder.Received(1).EmbedAsync("q", Arg.Any<CancellationToken>());
        await retriever.Received(1).RetrieveByVectorAsync(Embedding, Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
        await retriever.DidNotReceive().RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachedCacheHit_EmbedsOnce_AndDoesNotTouchRetriever()
    {
        _cache.GetAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Result("hit") });
        var retriever = Substitute.For<IVectorRetriever>();
        var cached = new CachedRetrievalPipeline(VectorPipeline(retriever), _embedder, _cache);

        await cached.QueryAsync("q");

        await _embedder.Received(1).EmbedAsync("q", Arg.Any<CancellationToken>());
        await retriever.DidNotReceive().RetrieveByVectorAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
        await retriever.DidNotReceive().RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CachedColdQuery_Hybrid_ReusesVectorForVectorArm_LexicalUsesQueryString()
    {
        var vectorRetriever = Substitute.For<IVectorRetriever>();
        vectorRetriever.RetrieveByVectorAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Result("V") });
        var lexicalRetriever = Substitute.For<ILexicalRetriever>();
        lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Result("L") });

        var hybrid = new HybridRetrievalPipeline(vectorRetriever, lexicalRetriever);
        var cached = new CachedRetrievalPipeline(hybrid, _embedder, _cache);

        await cached.QueryAsync("q");

        await _embedder.Received(1).EmbedAsync("q", Arg.Any<CancellationToken>());
        await vectorRetriever.Received(1).RetrieveByVectorAsync(Embedding, Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
        await vectorRetriever.DidNotReceive().RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
        await lexicalRetriever.Received(1).RetrieveAsync("q", Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryByVector_RetrieverWithoutVectorSeam_FallsBackToStringOverload()
    {
        var retriever = Substitute.For<IRetriever>(); // not an IVectorRetriever
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await VectorPipeline(retriever).QueryByVectorAsync(Embedding, "q", new RetrievalOptions());

        await retriever.Received(1).RetrieveAsync("q", Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
        await _embedder.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
