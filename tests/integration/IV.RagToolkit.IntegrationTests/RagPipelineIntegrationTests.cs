using FluentAssertions;
using IV.RagToolkit.IntegrationTests.Fixtures;
using IV.RagToolkit.IntegrationTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RagToolkit.IntegrationTests;

/// <summary>
/// Full pipeline test: ingest documents → query → verify relevant chunks returned.
///
/// Uses 3-dimensional unit vectors with known cosine similarities so retrieval
/// ordering is predictable without a real embedding model.
///   "cats are animals"  → [1, 0, 0]
///   "dogs are animals"  → [0.9, 0.436, 0]   sim to cats ≈ 0.9
///   "cars are vehicles" → [0, 1, 0]          sim to cats = 0.0
///   query "what are cats?" → [1, 0, 0]
/// </summary>
public sealed class RagPipelineIntegrationTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    public RagPipelineIntegrationTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private IRagPipeline CreatePipeline(string tableName, IEmbedder embedder)
    {
        var postgresOptions = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName,
            VectorDimension = 3
        });
        var chunkerOptions = Options.Create(new FixedSizeChunkerOptions { ChunkSize = 512 });

        var chunker = new FixedSizeChunker(chunkerOptions);
        var vectorStore = new PostgresVectorStore(_fixture.DataSource, postgresOptions);
        var retriever = new PostgresRetriever(_fixture.DataSource, postgresOptions);

        return new RagPipeline(chunker, embedder, vectorStore, retriever, NullLogger<RagPipeline>.Instance);
    }

    [Fact]
    public async Task IngestAndQuery_ReturnsChunksOrderedBySimilarity()
    {
        var embeddings = new Dictionary<string, float[]>
        {
            ["cats are animals"]  = [1f, 0f, 0f],
            ["dogs are animals"]  = [0.9f, 0.436f, 0f],
            ["cars are vehicles"] = [0f, 1f, 0f],
            ["what are cats?"]    = [1f, 0f, 0f]
        };
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable(), FakeEmbedder.FromDictionary(embeddings));

        await pipeline.IngestAsync(new Document("cats are animals"));
        await pipeline.IngestAsync(new Document("dogs are animals"));
        await pipeline.IngestAsync(new Document("cars are vehicles"));

        var results = await pipeline.QueryAsync("what are cats?", new RetrievalOptions { TopK = 3, MinScore = -1f });

        results.Should().HaveCount(3);
        results[0].Chunk.Text.Should().Be("cats are animals");
        results[1].Chunk.Text.Should().Be("dogs are animals");
        results[2].Chunk.Text.Should().Be("cars are vehicles");
    }

    [Fact]
    public async Task IngestAndQuery_DefaultOptions_FiltersIrrelevantChunks()
    {
        var embeddings = new Dictionary<string, float[]>
        {
            ["cats are animals"]  = [1f, 0f, 0f],
            ["cars are vehicles"] = [0f, 1f, 0f],
            ["what are cats?"]    = [1f, 0f, 0f]
        };
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable(), FakeEmbedder.FromDictionary(embeddings));

        await pipeline.IngestAsync(new Document("cats are animals"));
        await pipeline.IngestAsync(new Document("cars are vehicles"));

        // Default MinScore = 0.0 — "cars" (sim = 0.0) should be excluded
        var results = await pipeline.QueryAsync("what are cats?");

        results.Should().OnlyContain(r => r.Score > 0f);
    }

    [Fact]
    public async Task IngestAndQuery_TopKLimit_ReturnsCorrectCount()
    {
        var embeddings = new Dictionary<string, float[]>
        {
            ["cats are animals"]  = [1f, 0f, 0f],
            ["dogs are animals"]  = [0.9f, 0.436f, 0f],
            ["cars are vehicles"] = [0f, 1f, 0f],
            ["what are cats?"]    = [1f, 0f, 0f]
        };
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable(), FakeEmbedder.FromDictionary(embeddings));

        await pipeline.IngestAsync(new Document("cats are animals"));
        await pipeline.IngestAsync(new Document("dogs are animals"));
        await pipeline.IngestAsync(new Document("cars are vehicles"));

        var results = await pipeline.QueryAsync("what are cats?", new RetrievalOptions { TopK = 2, MinScore = -1f });

        results.Should().HaveCount(2);
    }
}
