using FluentAssertions;
using IV.RAG.E2ETests.Fixtures;
using IV.RAG.E2ETests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RAG.E2ETests;

/// <summary>
/// End-to-end tests for embedding migration against real Ollama and Postgres.
/// Requires Ollama running at http://localhost:11434 with nomic-embed-text loaded.
/// Run with: dotnet test IV.RAG.E2E.slnf
/// </summary>
public sealed class EmbeddingMigrationE2ETests : IClassFixture<PostgresContainerFixture>
{
    private const string OllamaEndpoint = "http://localhost:11434";
    private const string EmbeddingModel = "nomic-embed-text";

    private readonly PostgresContainerFixture _fixture;

    public EmbeddingMigrationE2ETests(PostgresContainerFixture fixture) => _fixture = fixture;

    private IEmbedder CreateEmbedder(string? version = null)
    {
        var factory = new SingletonHttpClientFactory(OllamaEndpoint);
        var opts = Options.Create(new OllamaOptions { Endpoint = OllamaEndpoint, EmbeddingModel = EmbeddingModel });
        IEmbedder embedder = new OllamaEmbedder(factory, opts);
        return version is null ? embedder : new VersionedEmbedder(embedder, version);
    }

    private (PostgresVectorStore Store, EmbeddingMigrator Migrator, PostgresRetriever Retriever) Create(
        string table, IEmbedder embedder)
    {
        var opts = Options.Create(new PostgresOptions
            { ConnectionString = _fixture.ConnectionString, TableName = table });
        var store = new PostgresVectorStore(_fixture.DataSource, embedder, opts);
        return (store, new EmbeddingMigrator(store, embedder), new PostgresRetriever(_fixture.DataSource, embedder, opts));
    }

    // ─── IsNeededAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task IsNeededAsync_FreshStore_ReturnsFalse()
    {
        var embedder = CreateEmbedder();
        var (store, migrator, _) = Create(PostgresContainerFixture.NewTable(), embedder);
        using var _store1 = store;

        var isNeeded = await migrator.IsNeededAsync();

        isNeeded.Should().BeFalse();
    }

    [Fact]
    public async Task IsNeededAsync_AfterIngestWithCurrentModel_ReturnsFalse()
    {
        var embedder = CreateEmbedder();
        var (store, migrator, _) = Create(PostgresContainerFixture.NewTable(), embedder);
        using var _store2 = store;

        var origin = new Document.Origin(new Guid("a1000000-0000-0000-0000-000000000001"), "E2E", "doc-1");
        var embedding = await embedder.EmbedAsync("Cats are domestic animals");
        await store.SetAsync(origin, [new Chunk { Id = "c1", Text = "Cats are domestic animals", Embedding = embedding, Origin = origin }]);

        var isNeeded = await migrator.IsNeededAsync();

        isNeeded.Should().BeFalse();
    }

    // ─── MigrateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MigrateAsync_AfterVersionChange_IsNeededReturnsFalse()
    {
        var table = PostgresContainerFixture.NewTable();

        var embedderV1 = CreateEmbedder("v1");
        var (storeV1, _, _) = Create(table, embedderV1);
        using (storeV1)
        {
            var origin1 = new Document.Origin(new Guid("a1000000-0000-0000-0000-000000000001"), "E2E", "doc-1");
            var embedding1 = await embedderV1.EmbedAsync("Cats are domestic animals");
            await storeV1.SetAsync(origin1, [new Chunk { Id = "c1", Text = "Cats are domestic animals", Embedding = embedding1, Origin = origin1 }]);

            var origin2 = new Document.Origin(new Guid("a2000000-0000-0000-0000-000000000002"), "E2E", "doc-2");
            var embedding2 = await embedderV1.EmbedAsync("Dogs are loyal companions");
            await storeV1.SetAsync(origin2, [new Chunk { Id = "c2", Text = "Dogs are loyal companions", Embedding = embedding2, Origin = origin2 }]);
        }

        var embedderV2 = CreateEmbedder("v2");
        var (storeV2, migratorV2, _) = Create(table, embedderV2);
        using var _storeV2a = storeV2;

        (await migratorV2.IsNeededAsync()).Should().BeTrue();

        var reports = new List<EmbeddingMigrationProgress>();
        var @lock = new object();
        await migratorV2.MigrateAsync(new SyncProgress<EmbeddingMigrationProgress>(p =>
        {
            lock (@lock) reports.Add(p);
        }));

        reports.Count.Should().Be(2);
        reports.Last().Processed.Should().Be(2);

        (await migratorV2.IsNeededAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task MigrateAsync_AfterVersionChange_RetrievalStillWorks()
    {
        var table = PostgresContainerFixture.NewTable();

        var embedderV1 = CreateEmbedder("v1");
        var (storeV1, _, _) = Create(table, embedderV1);
        using (storeV1)
        {
            var origin = new Document.Origin(new Guid("a1000000-0000-0000-0000-000000000001"), "E2E", "doc-1");
            var embedding = await embedderV1.EmbedAsync("Cats are domestic animals");
            await storeV1.SetAsync(origin, [new Chunk { Id = "c1", Text = "Cats are domestic animals", Embedding = embedding, Origin = origin }]);
        }

        var embedderV2 = CreateEmbedder("v2");
        var (storeV2, migratorV2, retrieverV2) = Create(table, embedderV2);
        using var _storeV2b = storeV2;

        await migratorV2.MigrateAsync();

        var results = await retrieverV2.RetrieveAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });

        results.Should().NotBeEmpty();
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    /// <summary>Wraps a real embedder but reports a custom model name — simulates a model version switch.</summary>
    private sealed class VersionedEmbedder(IEmbedder inner, string version) : IEmbedder
    {
        private readonly string _versionedName = $"{inner.ModelInfo.ModelName}-{version}";

        // Dimensions are read dynamically so auto-detection on the inner embedder propagates here.
        public EmbedderInfo ModelInfo =>
            new(inner.ModelInfo.Provider, _versionedName, inner.ModelInfo.Dimensions);

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
            inner.EmbedAsync(text, ct);
    }

    private sealed class PlainTextChunkerBridge : IChunker
    {
        private readonly IChunker<PlainTextDocument> _inner;
        public PlainTextChunkerBridge(IChunker<PlainTextDocument> inner) => _inner = inner;
        public IAsyncEnumerable<Chunk> ChunkAsync(Document doc, CancellationToken ct = default)
            => _inner.ChunkAsync((PlainTextDocument)doc, ct);
    }
}
