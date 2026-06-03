using System.Net.Sockets;
using System.Text.Json.Serialization;
using FluentAssertions;
using IV.RAG.E2ETests.Fixtures;
using IV.RAG.E2ETests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RAG.E2ETests;

/// <summary>
/// End-to-end tests for <see cref="RemoteRetrievalPipeline"/>.
/// Spins up an in-process ASP.NET Core minimal-API server backed by real Postgres + Ollama,
/// then queries it via <see cref="RemoteRetrievalPipeline"/> to verify the full HTTP transport.
///
/// Requires Ollama running at http://localhost:11434 with nomic-embed-text loaded.
/// Run with: dotnet test IV.RAG.E2E.slnf
/// </summary>
public sealed class RemoteRetrievalPipelineE2ETests : IClassFixture<PostgresContainerFixture>
{
    private const string OllamaEndpoint = "http://localhost:11434";
    private const string EmbeddingModel = "nomic-embed-text";
    private const int VectorDimension = 768;

    private readonly PostgresContainerFixture _fixture;

    public RemoteRetrievalPipelineE2ETests(PostgresContainerFixture fixture) => _fixture = fixture;

    private (IIngestionPipeline Ingestion, IRetrievalPipeline Retrieval) CreateServerPipeline(string tableName)
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
            EmbeddingModel = EmbeddingModel
        });
        var embedder = new OllamaEmbedder(httpFactory, ollamaOptions);
        var chunker = new PlainTextChunkerBridge(
            new FixedSizeChunker(Options.Create(new FixedSizeChunkerOptions { ChunkSize = 512 })));
        var vectorStore = new PostgresVectorStore(_fixture.DataSource, embedder, postgresOptions);
        var retriever = new PostgresRetriever(_fixture.DataSource, embedder, postgresOptions);
        var pipeline = new RetrievalPipeline(
            chunker, embedder, vectorStore, retriever,
            NullLogger<RetrievalPipeline>.Instance);
        return (pipeline, pipeline);
    }

    private static async Task<(WebApplication App, string BaseUrl)> StartServerAsync(IRetrievalPipeline retrieval)
    {
        var port = FindFreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(retrieval);

        var app = builder.Build();
        app.Urls.Add(baseUrl);

        app.MapPost("/api/query", async (ServerQueryRequest req, IRetrievalPipeline pipeline) =>
        {
            var opts = new RetrievalOptions
            {
                TopK = req.TopK,
                MinScore = req.MinScore,
                MetadataFilter = req.MetadataFilter
            };
            var results = await pipeline.QueryAsync(req.Query, opts);
            return new ServerQueryResponse(
                results.Select(r => new ServerSearchResult(
                    new ServerChunk(
                        r.Chunk.Id,
                        r.Chunk.Text,
                        r.Chunk.ChunkIndex,
                        new ServerOrigin(
                            r.Chunk.Origin.SourceId,
                            r.Chunk.Origin.DocumentType,
                            r.Chunk.Origin.DocumentId),
                        r.Chunk.Metadata),
                    r.Score)).ToArray());
        });

        await app.StartAsync();
        return (app, baseUrl);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ─── basic HTTP transport ─────────────────────────────────────────────────

    [Fact]
    public async Task QueryViaHttp_IngestedDocument_IsReturnedByRemotePipeline()
    {
        var (ingestion, serverRetrieval) = CreateServerPipeline(PostgresContainerFixture.NewTable());
        var (serverApp, baseUrl) = await StartServerAsync(serverRetrieval);
        await using var _ = serverApp;

        await ingestion.IngestAsync(new TestDocument("Cats are independent domestic animals", documentId: "cats"));
        await ingestion.IngestAsync(new TestDocument("JavaScript is used for web development", documentId: "js"));

        using var clientFactory = new FixedUrlHttpClientFactory(baseUrl);
        var client = new RemoteRetrievalPipeline(
            clientFactory,
            Options.Create(new RemoteOptions { Endpoint = baseUrl, QueryPath = "/api/query" }));

        var results = await client.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });

        results.Should().NotBeEmpty();
        results[0].Chunk.Text.Should().Contain("Cats");
    }

    // ─── metadata filter transmitted over HTTP ────────────────────────────────

    [Fact]
    public async Task QueryViaHttp_MetadataFilter_IsTransmittedAndApplied()
    {
        var (ingestion, serverRetrieval) = CreateServerPipeline(PostgresContainerFixture.NewTable());
        var (serverApp, baseUrl) = await StartServerAsync(serverRetrieval);
        await using var _ = serverApp;

        await ingestion.IngestAsync(new TestDocument(
            "Cats are domestic animals", documentId: "cats",
            metadata: new Metadata { ["type"] = "animal" }));
        await ingestion.IngestAsync(new TestDocument(
            "Dogs are loyal companions", documentId: "dogs",
            metadata: new Metadata { ["type"] = "animal" }));
        await ingestion.IngestAsync(new TestDocument(
            "Python is a programming language", documentId: "python",
            metadata: new Metadata { ["type"] = "tech" }));

        using var clientFactory = new FixedUrlHttpClientFactory(baseUrl);
        var client = new RemoteRetrievalPipeline(
            clientFactory,
            Options.Create(new RemoteOptions { Endpoint = baseUrl, QueryPath = "/api/query" }));

        var results = await client.QueryAsync("animals", new RetrievalOptions
        {
            TopK = 10,
            MinScore = -1f,
            MetadataFilter = MetadataFilter.Eq("type", "animal")
        });

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r =>
            r.Chunk.Metadata!["type"].Should().Be((MetadataFilterValue)"animal"),
            "metadata filter must be forwarded to the server and applied before returning results");
    }

    // ─── DI wiring ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ViaDI_AddAnswerPipeline_WithRemoteRetrieval_ResolvesAndReturnsResults()
    {
        var (ingestion, serverRetrieval) = CreateServerPipeline(PostgresContainerFixture.NewTable());
        var (serverApp, baseUrl) = await StartServerAsync(serverRetrieval);
        await using var _ = serverApp;

        await ingestion.IngestAsync(new TestDocument("Cats are domestic animals", documentId: "cats"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGenerator>(new NullGenerator());
        services.AddAnswerPipeline()
            .AddRemoteRetrievalPipeline(o =>
            {
                o.Endpoint = baseUrl;
                o.QueryPath = "/api/query";
            });

        await using var provider = services.BuildServiceProvider();

        var retrieval = provider.GetRequiredService<IRetrievalPipeline>();
        retrieval.Should().BeOfType<RemoteRetrievalPipeline>(
            "AddRemoteRetrievalPipeline should register RemoteRetrievalPipeline as IRetrievalPipeline");

        var results = await retrieval.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });
        results.Should().NotBeEmpty();

        // Verify the answer pipeline resolves and calls through to the remote retrieval
        var answer = await provider.GetRequiredService<IAnswerPipeline>()
            .AnswerAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });
        answer.Should().NotBeNull();
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private sealed class FixedUrlHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;
        internal FixedUrlHttpClientFactory(string baseUrl)
            => _client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        public HttpClient CreateClient(string name) => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class NullGenerator : IGenerator
    {
        public Task<string> GenerateAsync(string query, IReadOnlyList<SearchResult> chunks, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }

    private sealed class PlainTextChunkerBridge : IChunker
    {
        private readonly IChunker<PlainTextDocument> _inner;
        public PlainTextChunkerBridge(IChunker<PlainTextDocument> inner) => _inner = inner;
        public IAsyncEnumerable<Chunk> ChunkAsync(Document doc, CancellationToken ct = default)
            => _inner.ChunkAsync((PlainTextDocument)doc, ct);
    }

    // ─── server-side DTOs — JSON property names must match the client's contract ───

    private sealed record ServerQueryRequest(
        [property: JsonPropertyName("query")]          string Query,
        [property: JsonPropertyName("topK")]           int TopK,
        [property: JsonPropertyName("minScore")]       float MinScore,
        [property: JsonPropertyName("metadataFilter")] MetadataFilter? MetadataFilter);

    private sealed record ServerQueryResponse(
        [property: JsonPropertyName("results")] ServerSearchResult[] Results);

    private sealed record ServerSearchResult(
        [property: JsonPropertyName("chunk")] ServerChunk Chunk,
        [property: JsonPropertyName("score")] float Score);

    private sealed record ServerChunk(
        [property: JsonPropertyName("id")]         string? Id,
        [property: JsonPropertyName("text")]       string Text,
        [property: JsonPropertyName("chunkIndex")] int? ChunkIndex,
        [property: JsonPropertyName("origin")]     ServerOrigin Origin,
        [property: JsonPropertyName("metadata")]   Metadata? Metadata);

    private sealed record ServerOrigin(
        [property: JsonPropertyName("sourceId")]     Guid SourceId,
        [property: JsonPropertyName("documentType")] string DocumentType,
        [property: JsonPropertyName("documentId")]   string DocumentId);
}
