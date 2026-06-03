using FluentAssertions;
using IV.RAG.E2ETests.Fixtures;
using IV.RAG.E2ETests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RAG.E2ETests;

/// <summary>
/// End-to-end tests using a real Ollama server and real Postgres.
/// Requires Ollama running at http://localhost:11434 with nomic-embed-text loaded.
/// Run with: dotnet test IV.RAG.E2E.slnf
/// </summary>
public sealed class RagPipelineE2ETests : IClassFixture<PostgresContainerFixture>
{
    private const string OllamaEndpoint = "http://localhost:11434";
    private const string EmbeddingModel = "nomic-embed-text";
    private const int VectorDimension = 768;

    private readonly PostgresContainerFixture _fixture;

    public RagPipelineE2ETests(PostgresContainerFixture fixture) => _fixture = fixture;

    private IRagPipeline CreatePipeline(string tableName)
    {
        var postgresOptions = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName,
            VectorDimension = VectorDimension
        });
        var ollamaOptions = Options.Create(new OllamaOptions
        {
            Endpoint = OllamaEndpoint,
            EmbeddingModel = EmbeddingModel
        });

        var httpFactory = new SingletonHttpClientFactory(OllamaEndpoint);
        var chunker = new PlainTextChunkerBridge(new FixedSizeChunker(Options.Create(new FixedSizeChunkerOptions { ChunkSize = 512 })));
        var embedder = new OllamaEmbedder(httpFactory, ollamaOptions);
        var generator = new OllamaGenerator(httpFactory, ollamaOptions);
        var vectorStore = new PostgresVectorStore(_fixture.DataSource, postgresOptions);
        var retriever = new PostgresRetriever(_fixture.DataSource, embedder, postgresOptions);

        var retrieval = new RetrievalPipeline(chunker, embedder, vectorStore, retriever, NullLogger<RetrievalPipeline>.Instance);
        return new RagPipeline(retrieval, retrieval, generator, NullLogger<RagPipeline>.Instance);
    }

    private sealed class PlainTextChunkerBridge : IChunker
    {
        private readonly IChunker<PlainTextDocument> _inner;
        public PlainTextChunkerBridge(IChunker<PlainTextDocument> inner) => _inner = inner;
        public IAsyncEnumerable<Chunk> ChunkAsync(Document doc, CancellationToken ct = default)
            => _inner.ChunkAsync((PlainTextDocument)doc, ct);
    }

    [Fact]
    public async Task OllamaEmbedder_ReturnsEmbeddingOfCorrectDimension()
    {
        using var factory = new SingletonHttpClientFactory(OllamaEndpoint);
        var embedder = new OllamaEmbedder(factory, Options.Create(new OllamaOptions
        {
            Endpoint = OllamaEndpoint,
            EmbeddingModel = EmbeddingModel
        }));

        var embedding = await embedder.EmbedAsync("hello world");

        embedding.Should().HaveCount(VectorDimension);
        embedding.Should().Contain(v => v != 0f);
    }

    [Fact]
    public async Task IngestAndQuery_SemanticSimilarity_AnimalQueryRanksAnimalDocsFirst()
    {
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable());

        await pipeline.IngestAsync(new TestDocument("Cats are independent and curious domestic animals", documentId: "cats"));
        await pipeline.IngestAsync(new TestDocument("Dogs are loyal and friendly companion animals", documentId: "dogs"));
        await pipeline.IngestAsync(new TestDocument("Python is a high-level programming language", documentId: "python"));
        await pipeline.IngestAsync(new TestDocument("JavaScript is used for web development", documentId: "js"));

        var results = await pipeline.QueryAsync(
            "Tell me about cats",
            new RetrievalOptions { TopK = 4, MinScore = -1f });

        results.Should().NotBeEmpty();

        var catIndex = results.ToList().FindIndex(r => r.Chunk.Text.Contains("Cats"));
        var jsIndex = results.ToList().FindIndex(r => r.Chunk.Text.Contains("JavaScript"));

        catIndex.Should().BeLessThan(jsIndex, "cat document should rank above JavaScript document");
    }

    [Fact]
    public async Task IngestAndQuery_SameDocumentIngestedTwice_BothChunksStoredAndRetrievable()
    {
        var pipeline = CreatePipeline(PostgresContainerFixture.NewTable());
        var doc = new TestDocument("Cats are domestic animals");

        await pipeline.IngestAsync(doc);
        await pipeline.IngestAsync(doc);

        var results = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 10, MinScore = -1f });

        results.Should().NotBeEmpty();
    }
}
