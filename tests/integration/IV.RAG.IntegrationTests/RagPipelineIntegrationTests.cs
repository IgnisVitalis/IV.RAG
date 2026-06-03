using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

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

        var chunker = new PlainTextChunkerBridge(new FixedSizeChunker(chunkerOptions));
        var vectorStore = new PostgresVectorStore(_fixture.DataSource, postgresOptions);
        var retriever = new PostgresRetriever(_fixture.DataSource, embedder, postgresOptions);

        var retrieval = new RetrievalPipeline(chunker, embedder, vectorStore, retriever, NullLogger<RetrievalPipeline>.Instance);
        return new RagPipeline(retrieval, retrieval, new NullGenerator(), NullLogger<RagPipeline>.Instance);
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

        await pipeline.IngestAsync(new TestDocument("cats are animals", documentId: "cats"));
        await pipeline.IngestAsync(new TestDocument("dogs are animals", documentId: "dogs"));
        await pipeline.IngestAsync(new TestDocument("cars are vehicles", documentId: "cars"));

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

        await pipeline.IngestAsync(new TestDocument("cats are animals", documentId: "cats"));
        await pipeline.IngestAsync(new TestDocument("cars are vehicles", documentId: "cars"));

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

        await pipeline.IngestAsync(new TestDocument("cats are animals", documentId: "cats"));
        await pipeline.IngestAsync(new TestDocument("dogs are animals", documentId: "dogs"));
        await pipeline.IngestAsync(new TestDocument("cars are vehicles", documentId: "cars"));

        var results = await pipeline.QueryAsync("what are cats?", new RetrievalOptions { TopK = 2, MinScore = -1f });

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task IngestAndQuery_RetrievedChunksHaveOriginAndChunkIndex()
    {
        var embeddings = new Dictionary<string, float[]>
        {
            ["cats are animals"] = [1f, 0f, 0f],
            ["what are cats?"]   = [1f, 0f, 0f]
        };
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable(), FakeEmbedder.FromDictionary(embeddings));
        var doc = new TestDocument("cats are animals", documentId: "cats");

        await pipeline.IngestAsync(doc);

        var results = await pipeline.QueryAsync("what are cats?", new RetrievalOptions { TopK = 1, MinScore = -1f });

        results[0].Chunk.Origin.Should().Be(doc.Source);
        results[0].Chunk.ChunkIndex.Should().Be(0);
    }

    private IRagPipeline CreateDiPipeline(string tableName, IEmbedder embedder)
    {
        var postgresOptions = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName,
            VectorDimension = 3
        });

        var services = new ServiceCollection();
        services.AddSingleton<ILogger<RetrievalPipeline>>(NullLogger<RetrievalPipeline>.Instance);
        services.AddSingleton<ILogger<RagPipeline>>(NullLogger<RagPipeline>.Instance);
        services.AddSingleton(embedder);
        services.AddSingleton<IGenerator>(new NullGenerator());
        services.AddSingleton<IVectorStore>(_ => new PostgresVectorStore(_fixture.DataSource, postgresOptions));
        services.AddSingleton<IRetriever>(sp => new PostgresRetriever(_fixture.DataSource, sp.GetRequiredService<IEmbedder>(), postgresOptions));
        services.AddRagToolkit().AddPlainTextChunker();
        return services.BuildServiceProvider().GetRequiredService<IRagPipeline>();
    }

    [Fact]
    public async Task ReIngest_SameDocument_ReplacesContent()
    {
        var embeddings = new Dictionary<string, float[]>
        {
            ["original text"]  = [1f, 0f, 0f],
            ["updated text"]   = [1f, 0f, 0f],
            ["what is it?"]    = [1f, 0f, 0f]
        };
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable(), FakeEmbedder.FromDictionary(embeddings));

        await pipeline.IngestAsync(new TestDocument("original text", documentId: "doc-x"));
        await pipeline.IngestAsync(new TestDocument("updated text", documentId: "doc-x"));

        var results = await pipeline.QueryAsync("what is it?", new RetrievalOptions { TopK = 10, MinScore = -1f });

        results.Should().HaveCount(1);
        results[0].Chunk.Text.Should().Be("updated text");
    }

    [Fact]
    public async Task IngestAndQuery_ViaDI_DispatcherRoutesPlainTextDocument()
    {
        var embeddings = new Dictionary<string, float[]>
        {
            ["cats are animals"] = [1f, 0f, 0f],
            ["what are cats?"]   = [1f, 0f, 0f]
        };
        var pipeline = CreateDiPipeline(PostgresContainerFixture.NewTable(), FakeEmbedder.FromDictionary(embeddings));
        var doc = new PlainTextDocument
        {
            Source = new Document.Origin(new Guid("a0000000-0000-0000-0000-000000000001"), "Test", "cats"),
            Text = "cats are animals"
        };

        await pipeline.IngestAsync(doc);
        var results = await pipeline.QueryAsync("what are cats?", new RetrievalOptions { TopK = 1, MinScore = -1f });

        results.Should().HaveCount(1);
        results[0].Chunk.Text.Should().Be("cats are animals");
        results[0].Chunk.Origin.DocumentId.Should().Be("cats");
    }

    private sealed class PlainTextChunkerBridge : IChunker
    {
        private readonly IChunker<PlainTextDocument> _inner;
        public PlainTextChunkerBridge(IChunker<PlainTextDocument> inner) => _inner = inner;
        public IAsyncEnumerable<Chunk> ChunkAsync(Document doc, CancellationToken ct = default)
            => _inner.ChunkAsync((PlainTextDocument)doc, ct);
    }

    private sealed class NullGenerator : IGenerator
    {
        public Task<string> GenerateAsync(string query, IReadOnlyList<SearchResult> chunks, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }
}
