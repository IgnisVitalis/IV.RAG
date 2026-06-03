using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace IV.RAG.Tests;

public class RetrievalPipelineTests
{
    private readonly IChunker _chunker = Substitute.For<IChunker>();
    private readonly IEmbedder _embedder = Substitute.For<IEmbedder>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IRetriever _retriever = Substitute.For<IRetriever>();
    private readonly RetrievalPipeline _pipeline;

    public RetrievalPipelineTests()
    {
        _pipeline = new RetrievalPipeline(_chunker, _embedder, _vectorStore, _retriever, NullLogger<RetrievalPipeline>.Instance);
    }

    [Fact]
    public async Task IngestAsync_EmbedsEachChunk_ThenReplacesAll()
    {
        var doc = new TestDocument("text");
        var chunk = new Chunk { Text = "text", Origin = doc.Source };
        var embedding = new float[] { 0.1f, 0.2f };

        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(chunk));
        _embedder.EmbedAsync(chunk.Text, Arg.Any<CancellationToken>()).Returns(embedding);

        await _pipeline.IngestAsync(doc);

        await _vectorStore.Received(1).SetAsync(
            doc.Source,
            Arg.Is<IEnumerable<Chunk>>(c => c.Single().Embedding == embedding),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_AssignsIdToEachChunk()
    {
        var doc = new TestDocument("text");
        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(new Chunk { Text = "text", Origin = doc.Source }));
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        IEnumerable<Chunk>? replaced = null;
        _vectorStore.When(x => x.SetAsync(Arg.Any<Document.Origin>(), Arg.Any<IEnumerable<Chunk>>(), Arg.Any<CancellationToken>()))
            .Do(x => replaced = x.ArgAt<IEnumerable<Chunk>>(1).ToList());

        await _pipeline.IngestAsync(doc);

        replaced!.Single().Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task IngestAsync_MultipleChunks_EachGetsUniqueId()
    {
        var doc = new TestDocument("text");
        var chunks = new[]
        {
            new Chunk { Text = "a", Origin = doc.Source },
            new Chunk { Text = "b", Origin = doc.Source }
        };
        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(chunks));
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        IEnumerable<Chunk>? replaced = null;
        _vectorStore.When(x => x.SetAsync(Arg.Any<Document.Origin>(), Arg.Any<IEnumerable<Chunk>>(), Arg.Any<CancellationToken>()))
            .Do(x => replaced = x.ArgAt<IEnumerable<Chunk>>(1).ToList());

        await _pipeline.IngestAsync(doc);

        replaced!.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task IngestAsync_AssignsChunkIndexInOrder()
    {
        var doc = new TestDocument("text");
        var chunks = new[]
        {
            new Chunk { Text = "a", Origin = doc.Source },
            new Chunk { Text = "b", Origin = doc.Source },
            new Chunk { Text = "c", Origin = doc.Source }
        };
        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(chunks));
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        IEnumerable<Chunk>? replaced = null;
        _vectorStore.When(x => x.SetAsync(Arg.Any<Document.Origin>(), Arg.Any<IEnumerable<Chunk>>(), Arg.Any<CancellationToken>()))
            .Do(x => replaced = x.ArgAt<IEnumerable<Chunk>>(1).ToList());

        await _pipeline.IngestAsync(doc);

        replaced!.Select(c => c.ChunkIndex).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task IngestAsync_PassesDocumentOriginToReplace()
    {
        var doc = new TestDocument("text");
        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(new Chunk { Text = "text", Origin = doc.Source }));
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        await _pipeline.IngestAsync(doc);

        await _vectorStore.Received(1).SetAsync(
            doc.Source,
            Arg.Any<IEnumerable<Chunk>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_PassesQueryStringToRetriever()
    {
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await _pipeline.QueryAsync("question");

        await _retriever.Received(1).RetrieveAsync(
            "question",
            Arg.Any<RetrievalOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_NullOptions_PassesDefaultOptions()
    {
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await _pipeline.QueryAsync("question", null);

        await _retriever.Received(1).RetrieveAsync(
            Arg.Any<string>(),
            Arg.Is<RetrievalOptions>(o => o.TopK == 5 && o.MinScore == 0.0f),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_ReturnsResultsFromRetriever()
    {
        var origin = new TestDocument("result").Source;
        var expected = new[] { new SearchResult(new Chunk { Text = "result", Origin = origin }, 0.9f) };
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var results = await _pipeline.QueryAsync("question");

        results.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task QueryAsync_DoesNotCallEmbedder()
    {
        _retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await _pipeline.QueryAsync("question");

        await _embedder.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── cache invalidation ──────────────────────────────────────────────────

    [Fact]
    public async Task IngestAsync_WithQueryCache_CallsInvalidateByDocument()
    {
        var cache = Substitute.For<IQueryCache>();
        var pipeline = new RetrievalPipeline(
            _chunker, _embedder, _vectorStore, _retriever,
            NullLogger<RetrievalPipeline>.Instance, cache);

        var doc = new TestDocument("text");
        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(new Chunk { Text = "text", Origin = doc.Source }));
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        await pipeline.IngestAsync(doc);

        await cache.Received(1).InvalidateByDocumentAsync(doc.Source, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_WithoutQueryCache_DoesNotThrow()
    {
        var doc = new TestDocument("text");
        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(new Chunk { Text = "text", Origin = doc.Source }));
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        var act = () => _pipeline.IngestAsync(doc);

        await act.Should().NotThrowAsync();
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<Chunk> Chunks(params Chunk[] chunks)
    {
        foreach (var chunk in chunks)
            yield return chunk;
    }
#pragma warning restore CS1998
}
