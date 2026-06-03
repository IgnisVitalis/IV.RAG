using FluentAssertions;
using IV.RAG.E2ETests.Fixtures;
using IV.RAG.E2ETests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RAG.E2ETests;

/// <summary>
/// End-to-end tests for <see cref="RetrievalPipeline"/> used standalone —
/// without the <see cref="RagPipeline"/> wrapper and without a generator.
/// Verifies that ingest → query works correctly with real Ollama embeddings and Postgres.
///
/// Requires Ollama running at http://localhost:11434 with nomic-embed-text loaded.
/// Run with: dotnet test IV.RAG.E2E.slnf
/// </summary>
public sealed class RetrievalPipelineE2ETests : IClassFixture<PostgresContainerFixture>
{
    private const string OllamaEndpoint = "http://localhost:11434";
    private const string EmbeddingModel = "nomic-embed-text";
    private const int VectorDimension = 768;

    private readonly PostgresContainerFixture _fixture;

    public RetrievalPipelineE2ETests(PostgresContainerFixture fixture) => _fixture = fixture;

    private RetrievalPipeline CreatePipeline(string tableName)
    {
        var postgresOptions = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName,
            VectorDimension = VectorDimension
        });
        var httpFactory = new SingletonHttpClientFactory(OllamaEndpoint);
        var ollamaOptions = Options.Create(new OllamaOptions
        {
            Endpoint = OllamaEndpoint,
            EmbeddingModel = EmbeddingModel
        });
        var embedder = new OllamaEmbedder(httpFactory, ollamaOptions);
        var chunker = new PlainTextChunkerBridge(
            new FixedSizeChunker(Options.Create(new FixedSizeChunkerOptions { ChunkSize = 512 })));
        var vectorStore = new PostgresVectorStore(_fixture.DataSource, postgresOptions);
        var retriever = new PostgresRetriever(_fixture.DataSource, embedder, postgresOptions);
        return new RetrievalPipeline(
            chunker, embedder, vectorStore, retriever,
            NullLogger<RetrievalPipeline>.Instance);
    }

    // ─── smoke ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAndQuery_SemanticSimilarity_ReturnsRankedResults()
    {
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable());

        await pipeline.IngestAsync(new TestDocument("Cats are independent domestic animals", documentId: "cats"));
        await pipeline.IngestAsync(new TestDocument("Dogs are loyal companion animals", documentId: "dogs"));
        await pipeline.IngestAsync(new TestDocument("JavaScript is used for web development", documentId: "js"));

        var results = await pipeline.QueryAsync("Tell me about cats", new RetrievalOptions { TopK = 3, MinScore = -1f });

        results.Should().NotBeEmpty();
        var catIndex = results.ToList().FindIndex(r => r.Chunk.Text.Contains("Cats"));
        var jsIndex  = results.ToList().FindIndex(r => r.Chunk.Text.Contains("JavaScript"));
        catIndex.Should().BeLessThan(jsIndex, "cat document should rank above JavaScript for a cat query");
    }

    // ─── TopK ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAndQuery_TopK_LimitsReturnedResults()
    {
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable());

        for (var i = 1; i <= 5; i++)
            await pipeline.IngestAsync(new TestDocument($"Document {i} about animals and nature", documentId: $"doc{i}"));

        var results = await pipeline.QueryAsync("animals", new RetrievalOptions { TopK = 2, MinScore = -1f });

        results.Should().HaveCount(2, "TopK = 2 must cap the result list regardless of how many docs match");
    }

    // ─── MinScore ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAndQuery_HighMinScore_FiltersOutUnrelatedDocuments()
    {
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable());

        await pipeline.IngestAsync(new TestDocument("Cats are independent domestic animals", documentId: "cats"));
        await pipeline.IngestAsync(new TestDocument("Dogs are loyal companion animals", documentId: "dogs"));
        await pipeline.IngestAsync(new TestDocument("JavaScript is used for web development", documentId: "js"));
        await pipeline.IngestAsync(new TestDocument("Python is a high-level programming language", documentId: "python"));

        // Query about cats with a high MinScore threshold.
        // Animal docs should pass; programming docs should be filtered.
        var all    = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 10, MinScore = -1f });
        var strict = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 10, MinScore = 0.5f });

        strict.Count.Should().BeLessThan(all.Count,
            "a high MinScore threshold must exclude low-relevance documents");
        strict.Should().AllSatisfy(r =>
            r.Score.Should().BeGreaterThanOrEqualTo(0.5f));
    }

    // ─── DI wiring ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ViaDI_AddRetrievalPipeline_IngestionAndRetrievalShareTheSameInstance()
    {
        var tableName = PostgresContainerFixture.NewTable();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRetrievalPipeline()
            .AddPlainTextChunker()
            .AddOllamaEmbedder(o =>
            {
                o.Endpoint = OllamaEndpoint;
                o.EmbeddingModel = EmbeddingModel;
            })
            .AddPostgresVectorStore(o =>
            {
                o.ConnectionString = _fixture.ConnectionString;
                o.TableName = tableName;
                o.VectorDimension = VectorDimension;
            });

        await using var provider = services.BuildServiceProvider();

        var ingestion = provider.GetRequiredService<IIngestionPipeline>();
        var retrieval = provider.GetRequiredService<IRetrievalPipeline>();

        ingestion.Should().BeOfType<RetrievalPipeline>();
        retrieval.Should().BeOfType<RetrievalPipeline>();
        ingestion.Should().BeSameAs(retrieval,
            "AddRetrievalPipeline should bind IIngestionPipeline and IRetrievalPipeline to the same singleton");

        await ingestion.IngestAsync(new TestDocument("Cats are domestic animals", documentId: "cats"));

        var results = await retrieval.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });
        results.Should().NotBeEmpty();
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private sealed class PlainTextChunkerBridge : IChunker
    {
        private readonly IChunker<PlainTextDocument> _inner;
        public PlainTextChunkerBridge(IChunker<PlainTextDocument> inner) => _inner = inner;
        public IAsyncEnumerable<Chunk> ChunkAsync(Document doc, CancellationToken ct = default)
            => _inner.ChunkAsync((PlainTextDocument)doc, ct);
    }
}
