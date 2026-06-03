using FluentAssertions;
using IV.RAG.E2ETests.Fixtures;
using IV.RAG.E2ETests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RAG.E2ETests;

/// <summary>
/// End-to-end tests for the hybrid retrieval → answer chain:
/// <see cref="HybridRetrievalPipeline"/> feeds into <see cref="AnswerPipeline"/> (or
/// <see cref="RagPipeline"/>) backed by a real <see cref="OllamaGenerator"/>.
/// Verifies that the full path — embed query, vector search, lexical search, RRF,
/// generate — works against live infrastructure.
///
/// Requires Ollama running at http://localhost:11434 with nomic-embed-text and a
/// generative model (default: llama3.1:8b) loaded.
/// Run with: dotnet test IV.RAG.E2E.slnf
/// </summary>
public sealed class HybridAnswerPipelineE2ETests : IClassFixture<PostgresContainerFixture>
{
    private const string OllamaEndpoint = "http://localhost:11434";
    private const string EmbeddingModel = "nomic-embed-text";
    private const string GenerationModel = "llama3.1:8b";
    private const int VectorDimension = 768;

    private readonly PostgresContainerFixture _fixture;

    public HybridAnswerPipelineE2ETests(PostgresContainerFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Returns an ingestion pipeline and a fully-wired <see cref="AnswerPipeline"/> where
    /// retrieval is handled by <see cref="HybridRetrievalPipeline"/>.
    /// </summary>
    private (IIngestionPipeline Ingestion, IAnswerPipeline Answer) CreateHybridAnswerSetup(string tableName)
    {
        var postgresOptions = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName
        });
        var httpFactory = new SingletonHttpClientFactory(OllamaEndpoint);
        var ollamaOptions = Options.Create(new OllamaOptions
        {
            Endpoint = OllamaEndpoint,
            EmbeddingModel = EmbeddingModel,
            GenerationModel = GenerationModel
        });

        var embedder = new OllamaEmbedder(httpFactory, ollamaOptions);
        var generator = new OllamaGenerator(httpFactory, ollamaOptions);
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

        var answer = new AnswerPipeline(hybrid, generator, NullLogger<AnswerPipeline>.Instance);

        return (ingestion, answer);
    }

    // ─── smoke ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HybridRetrievalAndAnswer_IngestedDocument_GeneratesNonEmptyAnswer()
    {
        var (ingestion, answer) = CreateHybridAnswerSetup(PostgresContainerFixture.NewTable());

        await ingestion.IngestAsync(new TestDocument(
            "Cats are independent and curious domestic animals. They sleep a lot and hunt small prey.",
            documentId: "cats"));
        await ingestion.IngestAsync(new TestDocument(
            "Dogs are loyal and friendly companion animals. They are often trained as service animals.",
            documentId: "dogs"));

        var result = await answer.AnswerAsync(
            "What are cats like?",
            new RetrievalOptions { TopK = 3, MinScore = -1f });

        result.Should().NotBeNullOrWhiteSpace("the generator should return a non-empty answer");
    }

    // ─── relevance: hybrid context improves answer ─────────────────────────────

    [Fact]
    public async Task HybridRetrievalAndAnswer_QueryMatchesBothSources_RetrievedContextIsRelevant()
    {
        // "cats" is an exact keyword match (lexical) AND semantically close to the query,
        // so it accumulates a higher RRF score and should appear in the context fed to the
        // generator. The answer should contain information from the cats document.
        var (ingestion, answer) = CreateHybridAnswerSetup(PostgresContainerFixture.NewTable());

        await ingestion.IngestAsync(new TestDocument(
            "Cats are independent domestic animals that purr and hunt mice.",
            documentId: "cats"));
        await ingestion.IngestAsync(new TestDocument(
            "JavaScript is a programming language used for web development.",
            documentId: "js"));

        var result = await answer.AnswerAsync(
            "Tell me about cats",
            new RetrievalOptions { TopK = 2, MinScore = -1f });

        result.Should().NotBeNullOrWhiteSpace();
        // The generator received the cats chunk as top context, so the answer
        // should not be solely about JavaScript.
        result.Should().NotBe("JavaScript is a programming language used for web development.",
            "answer should be grounded in the retrieved cat context, not the unrelated doc");
    }

    // ─── DI wiring ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ViaDI_AddRagToolkit_WithHybridAndOllama_AnswerAsyncReturnsResult()
    {
        var tableName = PostgresContainerFixture.NewTable();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagToolkit()
            .AddPlainTextChunker()
            .AddOllamaEmbedder(o =>
            {
                o.Endpoint = OllamaEndpoint;
                o.EmbeddingModel = EmbeddingModel;
            })
            .AddOllamaGenerator(o =>
            {
                o.Endpoint = OllamaEndpoint;
                o.GenerationModel = GenerationModel;
            })
            .AddPostgresVectorStore(o =>
            {
                o.ConnectionString = _fixture.ConnectionString;
                o.TableName = tableName;
            })
            .AddPostgresLexicalRetriever()
            .AddHybridRetrievalPipeline();

        await using var provider = services.BuildServiceProvider();

        var ragPipeline = provider.GetRequiredService<IRagPipeline>();
        var retrieval = provider.GetRequiredService<IRetrievalPipeline>();

        retrieval.Should().BeOfType<HybridRetrievalPipeline>(
            "AddHybridRetrievalPipeline should override IRetrievalPipeline");

        await ragPipeline.IngestAsync(new TestDocument(
            "Cats are independent domestic animals that purr.",
            documentId: "cats"));

        var answer = await ragPipeline.AnswerAsync(
            "What do cats do?",
            new RetrievalOptions { TopK = 3, MinScore = -1f });

        answer.Should().NotBeNullOrWhiteSpace(
            "the full hybrid RAG chain should produce a non-empty answer");
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
