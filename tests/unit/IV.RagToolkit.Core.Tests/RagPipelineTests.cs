using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace IV.RagToolkit.Tests;

public class RagPipelineTests
{
    private readonly IChunker _chunker = Substitute.For<IChunker>();
    private readonly IEmbedder _embedder = Substitute.For<IEmbedder>();
    private readonly IVectorStore _vectorStore = Substitute.For<IVectorStore>();
    private readonly IRetriever _retriever = Substitute.For<IRetriever>();
    private readonly RagPipeline _pipeline;

    public RagPipelineTests()
    {
        _pipeline = new RagPipeline(_chunker, _embedder, _vectorStore, _retriever, NullLogger<RagPipeline>.Instance);
    }

    [Fact]
    public async Task IngestAsync_EmbedseachChunk_ThenUpsertsAll()
    {
        var doc = new Document("text");
        var chunk = new Chunk { Text = "text" };
        var embedding = new float[] { 0.1f, 0.2f };

        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(chunk));
        _embedder.EmbedAsync(chunk.Text, Arg.Any<CancellationToken>()).Returns(embedding);

        await _pipeline.IngestAsync(doc);

        await _vectorStore.Received(1).UpsertAsync(
            Arg.Is<IEnumerable<Chunk>>(c => c.Single().Embedding == embedding),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_AssignsIdToEachChunk()
    {
        var doc = new Document("text");
        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(new Chunk { Text = "text" }));
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        IEnumerable<Chunk>? upserted = null;
        _vectorStore.When(x => x.UpsertAsync(Arg.Any<IEnumerable<Chunk>>(), Arg.Any<CancellationToken>()))
            .Do(x => upserted = x.Arg<IEnumerable<Chunk>>().ToList());

        await _pipeline.IngestAsync(doc);

        upserted!.Single().Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task IngestAsync_MultipleChunks_EachGetsUniqueId()
    {
        var doc = new Document("text");
        var chunks = new[] { new Chunk { Text = "a" }, new Chunk { Text = "b" } };
        _chunker.ChunkAsync(doc, Arg.Any<CancellationToken>()).Returns(Chunks(chunks));
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        IEnumerable<Chunk>? upserted = null;
        _vectorStore.When(x => x.UpsertAsync(Arg.Any<IEnumerable<Chunk>>(), Arg.Any<CancellationToken>()))
            .Do(x => upserted = x.Arg<IEnumerable<Chunk>>().ToList());

        await _pipeline.IngestAsync(doc);

        upserted!.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task QueryAsync_EmbedsQuery_ThenCallsRetriever()
    {
        var embedding = new float[] { 0.1f, 0.2f };
        _embedder.EmbedAsync("question", Arg.Any<CancellationToken>()).Returns(embedding);
        _retriever.RetrieveAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await _pipeline.QueryAsync("question");

        await _retriever.Received(1).RetrieveAsync(
            embedding,
            Arg.Any<RetrievalOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_NullOptions_PassesDefaultOptions()
    {
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });
        _retriever.RetrieveAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        await _pipeline.QueryAsync("question", null);

        await _retriever.Received(1).RetrieveAsync(
            Arg.Any<float[]>(),
            Arg.Is<RetrievalOptions>(o => o.TopK == 5 && o.MinScore == 0.0f),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryAsync_ReturnsResultsFromRetriever()
    {
        var expected = new[] { new SearchResult(new Chunk { Text = "result" }, 0.9f) };
        _embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });
        _retriever.RetrieveAsync(Arg.Any<float[]>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var results = await _pipeline.QueryAsync("question");

        results.Should().BeEquivalentTo(expected);
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<Chunk> Chunks(params Chunk[] chunks)
    {
        foreach (var chunk in chunks)
            yield return chunk;
    }
#pragma warning restore CS1998
}
