using FluentAssertions;
using IV.RAG.E2ETests.Fixtures;
using IV.RAG.E2ETests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RAG.E2ETests;

/// <summary>
/// End-to-end tests for <see cref="HybridRetrievalPipeline"/> using a real Ollama server
/// and real Postgres. Verifies that vector and lexical retrieval work together correctly
/// when backed by live infrastructure.
///
/// Requires Ollama running at http://localhost:11434 with nomic-embed-text loaded.
/// Run with: dotnet test IV.RAG.E2E.slnf
/// </summary>
public sealed class HybridRetrievalPipelineE2ETests : IClassFixture<PostgresContainerFixture>
{
    private const string OllamaEndpoint = "http://localhost:11434";
    private const string EmbeddingModel = "nomic-embed-text";
    private const int VectorDimension = 768;

    private readonly PostgresContainerFixture _fixture;

    public HybridRetrievalPipelineE2ETests(PostgresContainerFixture fixture) => _fixture = fixture;

    private IOptions<PostgresOptions> PostgresOptions(string tableName) =>
        Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName
        });

    private IOptions<OllamaOptions> OllamaOptions() =>
        Options.Create(new OllamaOptions
        {
            Endpoint = OllamaEndpoint,
            EmbeddingModel = EmbeddingModel
        });

    /// <summary>
    /// Returns an ingestion pipeline and a hybrid retrieval pipeline sharing the same table.
    /// Ingestion uses the full RetrievalPipeline (chunker + embedder + vector store).
    /// Retrieval uses HybridRetrievalPipeline (vector + lexical).
    /// </summary>
    private (IIngestionPipeline Ingestion, IRetrievalPipeline Hybrid) CreateHybridSetup(string tableName)
    {
        var postgresOptions = PostgresOptions(tableName);
        var httpFactory = new SingletonHttpClientFactory(OllamaEndpoint);
        var embedder = new OllamaEmbedder(httpFactory, OllamaOptions());
        var chunker = new PlainTextChunkerBridge(
            new FixedSizeChunker(Options.Create(new FixedSizeChunkerOptions { ChunkSize = 512 })));

        var vectorStore = new PostgresVectorStore(_fixture.DataSource, embedder, postgresOptions);
        var vectorRetriever = new PostgresRetriever(_fixture.DataSource, embedder, postgresOptions);
        var lexicalRetriever = new PostgresLexicalRetriever(_fixture.DataSource, postgresOptions);

        var ingestion = new RetrievalPipeline(
            chunker, embedder, vectorStore, vectorRetriever,
            NullLogger<RetrievalPipeline>.Instance);

        var hybrid = new HybridRetrievalPipeline(
            vectorRetriever, lexicalRetriever,
            logger: NullLogger<HybridRetrievalPipeline>.Instance);

        return (ingestion, hybrid);
    }

    // ─── smoke ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAndQuery_HybridPipeline_ReturnsResults()
    {
        var (ingestion, hybrid) = CreateHybridSetup(PostgresContainerFixture.NewTable());

        await ingestion.IngestAsync(new TestDocument("Cats are independent domestic animals", documentId: "cats"));
        await ingestion.IngestAsync(new TestDocument("Dogs are loyal companion animals", documentId: "dogs"));

        var results = await hybrid.QueryAsync("animals", new RetrievalOptions { TopK = 5, MinScore = -1f });

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r =>
        {
            r.Chunk.Id.Should().NotBeNullOrEmpty();
            r.Chunk.Text.Should().NotBeNullOrEmpty();
            r.Score.Should().BeGreaterThan(0f);
        });
    }

    // ─── semantic ordering ────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAndQuery_SemanticQuery_TopResultIsRelevant()
    {
        var (ingestion, hybrid) = CreateHybridSetup(PostgresContainerFixture.NewTable());

        await ingestion.IngestAsync(new TestDocument("Cats are independent and curious domestic animals", documentId: "cats"));
        await ingestion.IngestAsync(new TestDocument("Dogs are loyal and friendly companion animals", documentId: "dogs"));
        await ingestion.IngestAsync(new TestDocument("Python is a high-level programming language", documentId: "python"));
        await ingestion.IngestAsync(new TestDocument("JavaScript is used for web development", documentId: "js"));

        var results = await hybrid.QueryAsync("Tell me about cats", new RetrievalOptions { TopK = 4, MinScore = -1f });

        results.Should().NotBeEmpty();
        var catIndex = results.ToList().FindIndex(r => r.Chunk.Text.Contains("Cats"));
        var jsIndex  = results.ToList().FindIndex(r => r.Chunk.Text.Contains("JavaScript"));
        catIndex.Should().BeLessThan(jsIndex, "cats document should rank above JavaScript document");
    }

    // ─── RRF boost from dual-source match ────────────────────────────────────

    [Fact]
    public async Task IngestAndQuery_DualSourceMatch_RanksAboveVectorOnlyMatch()
    {
        // "exact" doc contains "cats" verbatim → found by BOTH vector search (semantic) and
        // lexical search (keyword). RRF assigns it score 1/(k+rank_vector) + 1/(k+rank_lexical).
        //
        // "semantic" doc is about felines — semantically related to "cats" so it appears in
        // vector results, but "felines" does not lexically match "cats", so it is absent from
        // lexical results. RRF assigns it only 1/(k+rank_vector).
        //
        // Expected: "exact" ranks first because it accumulates RRF score from two sources.
        var (ingestion, hybrid) = CreateHybridSetup(PostgresContainerFixture.NewTable());

        await ingestion.IngestAsync(new TestDocument(
            "Cats are domestic animals that purr and make great pets", documentId: "exact"));
        await ingestion.IngestAsync(new TestDocument(
            "Felines are graceful and independent mammals often kept as companions", documentId: "semantic"));

        var results = await hybrid.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });

        results.Should().HaveCount(2);

        var exactRank    = results.ToList().FindIndex(r => r.Chunk.Origin.DocumentId == "exact");
        var semanticRank = results.ToList().FindIndex(r => r.Chunk.Origin.DocumentId == "semantic");

        exactRank.Should().BeLessThan(semanticRank,
            "document found by both vector and lexical search accumulates a higher RRF score");
    }

    // ─── DI wiring ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ViaDI_AddHybridRetrievalPipeline_WiresUpAndReturnsResults()
    {
        var tableName = PostgresContainerFixture.NewTable();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGenerator>(new NullGenerator());

        services.AddRagToolkit()
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
            })
            .AddPostgresLexicalRetriever()
            .AddHybridRetrievalPipeline();

        await using var provider = services.BuildServiceProvider();

        var ingestion = provider.GetRequiredService<IIngestionPipeline>();
        var retrieval = provider.GetRequiredService<IRetrievalPipeline>();

        retrieval.Should().BeOfType<HybridRetrievalPipeline>(
            "AddHybridRetrievalPipeline should override IRetrievalPipeline");

        await ingestion.IngestAsync(new TestDocument("Cats are domestic animals", documentId: "cats"));

        var results = await retrieval.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });

        results.Should().NotBeEmpty();
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
        public Task<string> GenerateAsync(string query, IReadOnlyList<SearchResult> chunks, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
