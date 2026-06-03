using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace IV.RAG.Tests;

public class HybridRetrievalPipelineTests
{
    private readonly IRetriever _vectorRetriever = Substitute.For<IRetriever>();
    private readonly ILexicalRetriever _lexicalRetriever = Substitute.For<ILexicalRetriever>();

    private HybridRetrievalPipeline Create(int rrfK = 1, int candidateMultiplier = 3, IReranker? reranker = null)
    {
        var options = Options.Create(new HybridRetrievalOptions { RrfK = rrfK, CandidateMultiplier = candidateMultiplier });
        return new HybridRetrievalPipeline(_vectorRetriever, _lexicalRetriever, reranker, options);
    }

    private static Chunk Chunk(string id) =>
        new() { Id = id, Text = id, Origin = new Document.Origin(Guid.NewGuid(), "Test", id) };

    private static SearchResult Result(string id) => new(Chunk(id), 0f);

    // ─── RRF fusion ──────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_ChunkInBothLists_ScoresHigherThanChunkInOneList()
    {
        // vector: [A, B, C], lexical: [B, D]  →  B is in both, should rank first
        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("A"), Result("B"), Result("C")]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("B"), Result("D")]);

        var results = await Create(rrfK: 1).QueryAsync("q", new RetrievalOptions { TopK = 4 });

        results[0].Chunk.Id.Should().Be("B");
    }

    [Fact]
    public async Task QueryAsync_RRF_ScoresAreCorrect()
    {
        // With k=1:
        // vector: [A r0, B r1, C r2], lexical: [C r0, B r1]
        // A: 1/(1+1)            = 0.500
        // B: 1/(1+2) + 1/(1+2) = 0.667
        // C: 1/(1+3) + 1/(1+1) = 0.750
        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("A"), Result("B"), Result("C")]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("C"), Result("B")]);

        var results = await Create(rrfK: 1).QueryAsync("q", new RetrievalOptions { TopK = 3 });

        results.Select(r => r.Chunk.Id).Should().Equal("C", "B", "A");
        results[0].Score.Should().BeApproximately(0.75f, 0.001f);
        results[1].Score.Should().BeApproximately(0.667f, 0.001f);
        results[2].Score.Should().BeApproximately(0.5f, 0.001f);
    }

    [Fact]
    public async Task QueryAsync_TopK_LimitsReturnedResults()
    {
        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("A"), Result("B"), Result("C")]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("D"), Result("E"), Result("F")]);

        var results = await Create().QueryAsync("q", new RetrievalOptions { TopK = 2 });

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_NoOverlap_ReturnsTopKAcrossAllChunks()
    {
        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("A"), Result("B")]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("C"), Result("D")]);

        var results = await Create().QueryAsync("q", new RetrievalOptions { TopK = 4 });

        results.Select(r => r.Chunk.Id).Should().BeEquivalentTo(["A", "B", "C", "D"]);
    }

    [Fact]
    public async Task QueryAsync_ExpandsCandidatesBeforeFusion()
    {
        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await Create(candidateMultiplier: 4).QueryAsync("q", new RetrievalOptions { TopK = 5 });

        await _vectorRetriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o.TopK == 20), // 5 * 4
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_RunsBothRetrieversInParallel()
    {
        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await Create().QueryAsync("q", new RetrievalOptions { TopK = 5 });

        await _vectorRetriever.Received(1).RetrieveAsync("q", Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
        await _lexicalRetriever.Received(1).RetrieveAsync("q", Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_NullOptions_UsesDefaults()
    {
        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await Create().QueryAsync("q");

        await _vectorRetriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o.TopK == 15), // default TopK=5 * multiplier=3
            Arg.Any<CancellationToken>());
    }

    // ─── Reranker ────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_WithReranker_PassesFullFusedListToReranker()
    {
        var reranker = Substitute.For<IReranker>();
        var origin = new Document.Origin(Guid.NewGuid(), "Test", "x");
        var reranked = new[] { new SearchResult(new Chunk { Id = "B", Text = "B", Origin = origin }, 0.99f) };

        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("A"), Result("B")]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("B"), Result("C")]);
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(reranked);

        var results = await Create(reranker: reranker).QueryAsync("q", new RetrievalOptions { TopK = 1 });

        await reranker.Received(1).RerankAsync(
            "q",
            Arg.Is<IReadOnlyList<SearchResult>>(r => r.Count == 3), // A, B, C — full fused list
            1,
            Arg.Any<CancellationToken>());
        results.Should().BeEquivalentTo(reranked);
    }

    [Fact]
    public async Task QueryAsync_WithoutReranker_ReturnsFusedAndTrimmed()
    {
        _vectorRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("A"), Result("B")]);
        _lexicalRetriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns([Result("A"), Result("C")]);

        var results = await Create(reranker: null).QueryAsync("q", new RetrievalOptions { TopK = 5 });

        results.Select(r => r.Chunk.Id).Should().BeEquivalentTo(["A", "B", "C"]);
    }
}
