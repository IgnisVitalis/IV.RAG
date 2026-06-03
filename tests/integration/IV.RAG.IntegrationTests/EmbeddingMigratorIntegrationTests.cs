using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

public sealed class EmbeddingMigratorIntegrationTests : IClassFixture<PostgresContainerFixture>
{
    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] VecA = [1f, 0f, 0f];
    private static readonly float[] VecB = [0f, 1f, 0f];

    private static readonly Document.Origin Origin =
        new(new Guid("e0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    public EmbeddingMigratorIntegrationTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private (PostgresVectorStore Store, EmbeddingMigrator Migrator) Create(string table, FakeEmbedder embedder)
    {
        var options = Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table });
        var store = new PostgresVectorStore(_fixture.DataSource, embedder, options);
        return (store, new EmbeddingMigrator(store, embedder));
    }

    private PostgresRetriever CreateRetriever(string table, FakeEmbedder embedder) =>
        new(_fixture.DataSource, embedder, Options.Create(new PostgresOptions
            { ConnectionString = _fixture.ConnectionString, TableName = table }));

    [Fact]
    public async Task IsNeededAsync_FreshEmptyStore_ReturnsFalse()
    {
        var table = PostgresContainerFixture.NewTable();
        var embedder = new FakeEmbedder(_ => VecA);
        var (store, migrator) = Create(table, embedder);
        using var _ = store;

        var result = await migrator.IsNeededAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsNeededAsync_ChunksFromCurrentModel_ReturnsFalse()
    {
        var table = PostgresContainerFixture.NewTable();
        var embedder = new FakeEmbedder(_ => VecA, modelName: "v1");
        var (store, migrator) = Create(table, embedder);
        using var _ = store;

        await store.SetAsync(Origin, [new Chunk { Id = "1", Text = "hello", Embedding = VecA, Origin = Origin }]);

        var result = await migrator.IsNeededAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsNeededAsync_AfterModelChange_ReturnsTrue()
    {
        var table = PostgresContainerFixture.NewTable();

        var embedderV1 = new FakeEmbedder(_ => VecA, modelName: "v1");
        using (var storeV1 = new PostgresVectorStore(_fixture.DataSource, embedderV1,
            Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table })))
        {
            await storeV1.SetAsync(Origin, [new Chunk { Id = "1", Text = "hello", Embedding = VecA, Origin = Origin }]);
        }

        var embedderV2 = new FakeEmbedder(_ => VecB, modelName: "v2");
        var (storeV2, migrator) = Create(table, embedderV2);
        using var _2 = storeV2;

        var result = await migrator.IsNeededAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task MigrateAsync_NoOutdatedChunks_NoProgress()
    {
        var table = PostgresContainerFixture.NewTable();
        var embedder = new FakeEmbedder(_ => VecA);
        var (store, migrator) = Create(table, embedder);
        using var _ = store;

        var reports = new List<EmbeddingMigrationProgress>();
        var progress = new SyncProgress<EmbeddingMigrationProgress>(p => reports.Add(p));

        await migrator.MigrateAsync(progress);

        reports.Should().BeEmpty();
    }

    [Fact]
    public async Task MigrateAsync_ReEmbedsOutdatedChunks_RetrievalWorksWithNewModel()
    {
        var table = PostgresContainerFixture.NewTable();

        var embedderV1 = new FakeEmbedder(_ => VecA, modelName: "v1");
        using (var storeV1 = new PostgresVectorStore(_fixture.DataSource, embedderV1,
            Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table })))
        {
            await storeV1.SetAsync(Origin,
            [
                new Chunk { Id = "1", Text = "chunk-one", Embedding = VecA, Origin = Origin, ChunkIndex = 0 },
                new Chunk { Id = "2", Text = "chunk-two", Embedding = VecA, Origin = Origin, ChunkIndex = 1 }
            ]);
        }

        var embedderV2 = new FakeEmbedder(_ => VecB, modelName: "v2");
        var (storeV2, migrator) = Create(table, embedderV2);
        using var _ = storeV2;
        var retriever = CreateRetriever(table, embedderV2);

        var resultsBefore = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });
        resultsBefore.Should().BeEmpty();

        await migrator.MigrateAsync();

        var resultsAfter = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });
        resultsAfter.Should().HaveCount(2);
    }

    [Fact]
    public async Task MigrateAsync_ReportsProgressPerChunk()
    {
        var table = PostgresContainerFixture.NewTable();

        var embedderV1 = new FakeEmbedder(_ => VecA, modelName: "v1");
        using (var storeV1 = new PostgresVectorStore(_fixture.DataSource, embedderV1,
            Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table })))
        {
            await storeV1.SetAsync(Origin,
            [
                new Chunk { Id = "1", Text = "a", Embedding = VecA, Origin = Origin, ChunkIndex = 0 },
                new Chunk { Id = "2", Text = "b", Embedding = VecA, Origin = Origin, ChunkIndex = 1 },
                new Chunk { Id = "3", Text = "c", Embedding = VecA, Origin = Origin, ChunkIndex = 2 }
            ]);
        }

        var embedderV2 = new FakeEmbedder(_ => VecB, modelName: "v2");
        var (storeV2, migrator) = Create(table, embedderV2);
        using var _ = storeV2;

        var reports = new List<EmbeddingMigrationProgress>();
        var lockObj = new object();
        var progress = new SyncProgress<EmbeddingMigrationProgress>(p =>
        {
            lock (lockObj) reports.Add(p);
        });

        await migrator.MigrateAsync(progress);

        reports.Should().HaveCount(3);
        reports.Should().OnlyContain(r => r.Total == 3);
        reports.Select(r => r.Processed).Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public async Task MigrateAsync_AfterMigration_IsNeededReturnsFalse()
    {
        var table = PostgresContainerFixture.NewTable();

        var embedderV1 = new FakeEmbedder(_ => VecA, modelName: "v1");
        using (var storeV1 = new PostgresVectorStore(_fixture.DataSource, embedderV1,
            Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table })))
        {
            await storeV1.SetAsync(Origin, [new Chunk { Id = "1", Text = "hello", Embedding = VecA, Origin = Origin }]);
        }

        var embedderV2 = new FakeEmbedder(_ => VecB, modelName: "v2");
        var (storeV2, migrator) = Create(table, embedderV2);
        using var _ = storeV2;

        await migrator.MigrateAsync();

        var result = await migrator.IsNeededAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task MigrateAsync_DimensionChange_ReEmbedsWithNewDimension()
    {
        var table = PostgresContainerFixture.NewTable();

        var embedder3D = new FakeEmbedder(_ => [1f, 0f, 0f], dimensions: 3, modelName: "model-3d");
        using (var store3D = new PostgresVectorStore(_fixture.DataSource, embedder3D,
            Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table })))
        {
            await store3D.SetAsync(Origin, [new Chunk { Id = "1", Text = "hello", Embedding = [1f, 0f, 0f], Origin = Origin }]);
        }

        var embedder4D = new FakeEmbedder(_ => [0f, 0f, 1f, 0f], dimensions: 4, modelName: "model-4d");
        var (store4D, migrator) = Create(table, embedder4D);
        using var _ = store4D;

        await migrator.MigrateAsync();

        var isNeeded = await migrator.IsNeededAsync();
        isNeeded.Should().BeFalse();

        var retriever = CreateRetriever(table, embedder4D);
        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10, MinScore = -1f });
        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task MismatchException_CarriesCorrectModelInfo()
    {
        var table = PostgresContainerFixture.NewTable();

        var embedderA = new FakeEmbedder(_ => VecA, dimensions: 3, modelName: "model-a");
        using (var storeA = new PostgresVectorStore(_fixture.DataSource, embedderA,
            Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table })))
        {
            await storeA.SetAsync(Origin, [new Chunk { Id = "1", Text = "hello", Embedding = VecA, Origin = Origin }]);
        }

        var embedderB = new FakeEmbedder(_ => VecB, dimensions: 3, modelName: "model-b");
        using var storeB = new PostgresVectorStore(_fixture.DataSource, embedderB,
            Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table }));

        var act = async () => { await foreach (var _ in storeB.GetOutdatedAsync()) break; };

        var ex = await act.Should().ThrowAsync<EmbeddingModelMismatchException>();
        ex.Which.StoredModel.Should().Be(new EmbedderInfo("fake", "model-a", 3));
        ex.Which.CurrentModel.Should().Be(new EmbedderInfo("fake", "model-b", 3));
        ex.Which.TableName.Should().Be(table);
    }
}
