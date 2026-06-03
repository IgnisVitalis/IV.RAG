using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace IV.RAG.IntegrationTests;

public sealed class PostgresVectorStoreModelTrackingTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly Document.Origin TestOrigin =
        new(new Guid("f0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    public PostgresVectorStoreModelTrackingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private PostgresVectorStore CreateStore(string tableName, IEmbedder embedder) =>
        new(_fixture.DataSource, embedder, Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName
        }));

    [Fact]
    public async Task EnsureSchema_FirstUse_CreatesBothTables()
    {
        var tableName = PostgresContainerFixture.NewTable();
        var embedder = new FakeEmbedder(_ => [1f, 0f, 0f], dimensions: 3);
        using var store = CreateStore(tableName, embedder);

        await store.SetAsync(TestOrigin, []);

        await using var cmd = _fixture.DataSource.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_name IN (@chunks, @models) AND table_schema = current_schema()
            """;
        cmd.Parameters.AddWithValue("chunks", tableName);
        cmd.Parameters.AddWithValue("models", $"{tableName}_models");
        var count = (long)(await cmd.ExecuteScalarAsync())!;

        count.Should().Be(2);
    }

    [Fact]
    public async Task SetAsync_StoresModelId_MatchingCurrentEmbedder()
    {
        var tableName = PostgresContainerFixture.NewTable();
        var embedder = new FakeEmbedder(_ => [1f, 0f, 0f], dimensions: 3, modelName: "my-model");
        using var store = CreateStore(tableName, embedder);

        await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "1", Text = "hello", Embedding = [1f, 0f, 0f], Origin = TestOrigin }
        ]);

        await using var modelIdCmd = _fixture.DataSource.CreateCommand();
        modelIdCmd.CommandText = $"SELECT model_id FROM {tableName} WHERE id = '1'";
        var modelId = (int)(await modelIdCmd.ExecuteScalarAsync())!;

        await using var modelNameCmd = _fixture.DataSource.CreateCommand();
        modelNameCmd.CommandText = $"SELECT model_name FROM {tableName}_models WHERE id = @modelId";
        modelNameCmd.Parameters.AddWithValue("modelId", NpgsqlDbType.Integer, modelId);
        var modelName = (string)(await modelNameCmd.ExecuteScalarAsync())!;

        modelName.Should().Be("my-model");
    }

    [Fact]
    public async Task EnsureSchema_ModelMismatch_ThrowsWithCorrectStoredModel()
    {
        var tableName = PostgresContainerFixture.NewTable();
        var embedderA = new FakeEmbedder(_ => [1f, 0f, 0f], dimensions: 3, modelName: "model-a");
        using var storeA = CreateStore(tableName, embedderA);

        await storeA.SetAsync(TestOrigin,
        [
            new Chunk { Id = "1", Text = "hello", Embedding = [1f, 0f, 0f], Origin = TestOrigin }
        ]);

        var embedderB = new FakeEmbedder(_ => [1f, 0f, 0f], dimensions: 3, modelName: "model-b");
        using var storeB = CreateStore(tableName, embedderB);

        var act = async () => await storeB.SetAsync(TestOrigin, []);

        var ex = await act.Should().ThrowAsync<EmbeddingModelMismatchException>();
        ex.Which.StoredModel.Should().Be(new EmbedderInfo("fake", "model-a", 3));
        ex.Which.CurrentModel.Should().Be(new EmbedderInfo("fake", "model-b", 3));
        ex.Which.TableName.Should().Be(tableName);
    }

    [Fact]
    public async Task EnsureSchema_AfterMigration_ModelIdIsNotNull()
    {
        var tableName = PostgresContainerFixture.NewTable();
        var embedderV1 = new FakeEmbedder(_ => [1f, 0f, 0f], dimensions: 3, modelName: "v1");
        using var storeV1 = CreateStore(tableName, embedderV1);

        await storeV1.SetAsync(TestOrigin,
        [
            new Chunk { Id = "1", Text = "first",  Embedding = [1f, 0f, 0f], Origin = TestOrigin },
            new Chunk { Id = "2", Text = "second", Embedding = [1f, 0f, 0f], Origin = TestOrigin }
        ]);

        var embedderV2 = new FakeEmbedder(_ => [1f, 0f, 0f], dimensions: 3, modelName: "v2");
        using var storeForMigration = CreateStore(tableName, embedderV2);
        var migrator = new EmbeddingMigrator(storeForMigration, embedderV2);
        await migrator.MigrateAsync();

        using var storeV2 = CreateStore(tableName, embedderV2);
        await storeV2.SetAsync(TestOrigin, []);

        await using var cmd = _fixture.DataSource.CreateCommand();
        cmd.CommandText = """
            SELECT pa.attnotnull
            FROM pg_attribute pa
            WHERE pa.attrelid = to_regclass(@tableName)
              AND pa.attname = 'model_id'
              AND pa.attnum > 0
              AND NOT pa.attisdropped
            """;
        cmd.Parameters.AddWithValue("tableName", tableName);
        var attnotnull = (bool)(await cmd.ExecuteScalarAsync())!;

        attnotnull.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureSchema_DimensionChange_AltersColumnAndThrows()
    {
        var tableName = PostgresContainerFixture.NewTable();
        var embedder3d = new FakeEmbedder(_ => [1f, 0f, 0f], dimensions: 3, modelName: "dim3");
        using var store3d = CreateStore(tableName, embedder3d);

        await store3d.SetAsync(TestOrigin,
        [
            new Chunk { Id = "1", Text = "hello", Embedding = [1f, 0f, 0f], Origin = TestOrigin }
        ]);

        var embedder4d = new FakeEmbedder(_ => [0f, 0f, 1f, 0f], dimensions: 4, modelName: "dim4");
        using var store4dFirst = CreateStore(tableName, embedder4d);

        var act = async () => await store4dFirst.SetAsync(TestOrigin, []);

        await act.Should().ThrowAsync<EmbeddingModelMismatchException>();

        var migrator = new EmbeddingMigrator(store4dFirst, embedder4d);
        await migrator.MigrateAsync();

        var retriever = new PostgresRetriever(_fixture.DataSource, embedder4d, Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName
        }));
        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10, MinScore = -1f });
        results.Should().HaveCount(1);

        await using var cmd = _fixture.DataSource.CreateCommand();
        cmd.CommandText = """
            SELECT pa.atttypmod FROM pg_attribute pa
            WHERE pa.attrelid = to_regclass(@tableName) AND pa.attname = 'embedding'
              AND pa.attnum > 0 AND NOT pa.attisdropped
            """;
        cmd.Parameters.AddWithValue("tableName", tableName);
        var atttypmod = (int)(await cmd.ExecuteScalarAsync())!;

        atttypmod.Should().Be(4);
    }
}
