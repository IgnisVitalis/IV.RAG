using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

public sealed class PostgresSchemaManagementTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] Embedding = [1f, 0f, 0f];

    private static readonly Document.Origin TestOrigin =
        new(new Guid("d1000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    public PostgresSchemaManagementTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private IOptions<PostgresOptions> Options_(string table, SchemaManagementMode mode) =>
        Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = table,
            SchemaManagement = mode
        });

    private PostgresVectorStore CreateStore(string table, SchemaManagementMode mode) =>
        new(_fixture.DataSource, new FakeEmbedder(_ => Embedding, dimensions: 3), Options_(table, mode));

    private async Task<bool> IndexExistsAsync(string indexName)
    {
        await using var cmd = _fixture.DataSource.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@idx) IS NOT NULL";
        cmd.Parameters.AddWithValue("idx", indexName);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<long> ScalarCountAsync(string sql)
    {
        await using var cmd = _fixture.DataSource.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // Provisions the chunks + models tables the way SchemaManagement.Auto would, but deliberately
    // omits the embedding ANN index so a None run can be shown not to create it.
    private async Task ProvisionSchemaWithoutVectorIndexAsync(string table)
    {
        await using var cmd = _fixture.DataSource.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE {table}_models (
                id         SERIAL PRIMARY KEY,
                provider   TEXT NOT NULL,
                model_name TEXT NOT NULL,
                dimensions INT  NOT NULL,
                UNIQUE (provider, model_name, dimensions)
            );
            CREATE TABLE {table} (
                id            TEXT PRIMARY KEY,
                text          TEXT NOT NULL,
                embedding     vector(3) NOT NULL,
                metadata      JSONB,
                source_id     UUID NOT NULL,
                document_type TEXT NOT NULL,
                document_id   TEXT NOT NULL,
                chunk_index   INT,
                model_id      INT REFERENCES {table}_models(id),
                text_search   TSVECTOR GENERATED ALWAYS AS (to_tsvector('english'::regconfig, text)) STORED
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task None_TableAbsent_ThrowsClearError()
    {
        var table = PostgresContainerFixture.NewTable();
        using var store = CreateStore(table, SchemaManagementMode.None);

        var act = async () => await store.SetAsync(TestOrigin,
            [new Chunk { Id = "1", Text = "x", Embedding = Embedding, Origin = TestOrigin }]);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain(table).And.Contain("SchemaManagement is None");
    }

    [Fact]
    public async Task None_ProvisionedTable_InsertsWithoutCreatingVectorIndex()
    {
        var table = PostgresContainerFixture.NewTable();
        await ProvisionSchemaWithoutVectorIndexAsync(table);

        using var store = CreateStore(table, SchemaManagementMode.None);
        await store.SetAsync(TestOrigin,
            [new Chunk { Id = "1", Text = "cats", Embedding = Embedding, Origin = TestOrigin }]);

        // Chunk was inserted and the model row was upserted (DML allowed under None)...
        var retriever = new PostgresRetriever(_fixture.DataSource, new FakeEmbedder(_ => Embedding, dimensions: 3), Options_(table, SchemaManagementMode.None));
        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 1 });
        results.Should().ContainSingle().Which.Chunk.Id.Should().Be("1");

        // ...but no structural DDL ran, so the ANN index was not created.
        (await IndexExistsAsync($"{table}_embedding_idx")).Should().BeFalse();
    }

    [Fact]
    public async Task Auto_ConcurrentFirstUse_AcrossInstances_InitializesOnce()
    {
        var table = PostgresContainerFixture.NewTable();

        // Each store has its own in-process SemaphoreSlim, so only the cross-process advisory lock
        // serializes their schema DDL. Distinct origins keep the data writes independent.
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            using var store = CreateStore(table, SchemaManagementMode.Auto);
            var origin = new Document.Origin(new Guid($"d2000000-0000-0000-0000-{i:D12}"), "Test", $"doc-{i}");
            await store.SetAsync(origin,
                [new Chunk { Id = $"chunk-{i}", Text = $"text {i}", Embedding = Embedding, Origin = origin }]);
        });

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        (await IndexExistsAsync($"{table}_embedding_idx")).Should().BeTrue();
        (await ScalarCountAsync($"SELECT COUNT(*) FROM {table}_models")).Should().Be(1);
        (await ScalarCountAsync($"SELECT COUNT(*) FROM {table}")).Should().Be(8);
    }

    // ── Query cache ──────────────────────────────────────────────────────────

    private PostgresQueryCache CreateCache(string table, SchemaManagementMode mode) =>
        new(_fixture.DataSource,
            new FakeEmbedder(_ => Embedding, dimensions: 3),
            Options.Create(new PostgresOptions
            {
                ConnectionString = _fixture.ConnectionString,
                QueryCacheTableName = table,
                SchemaManagement = mode
            }),
            Options.Create(new QueryCacheOptions { SimilarityThreshold = 0.95f, Ttl = TimeSpan.FromHours(1) }));

    [Fact]
    public async Task Cache_None_TableAbsent_ThrowsClearError()
    {
        var table = PostgresContainerFixture.NewTable();
        using var cache = CreateCache(table, SchemaManagementMode.None);

        var act = async () => await cache.GetAsync(Embedding, new RetrievalOptions());

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain(table).And.Contain("SchemaManagement is None");
    }

    [Fact]
    public async Task Cache_Auto_ConcurrentFirstUse_AcrossInstances_InitializesOnce()
    {
        var table = PostgresContainerFixture.NewTable();
        var origin = new Document.Origin(new Guid("d3000000-0000-0000-0000-000000000001"), "Test", "doc");

        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            using var cache = CreateCache(table, SchemaManagementMode.Auto);
            float[] embedding = [1f, i * 0.01f, 0f];
            IReadOnlyList<SearchResult> results =
                [new SearchResult(new Chunk { Id = $"c{i}", Text = "t", Origin = origin }, 0.9f)];
            await cache.SetAsync(embedding, new RetrievalOptions { TopK = i + 1 }, results);
        });

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();

        (await ScalarCountAsync($"SELECT COUNT(*) FROM {table}")).Should().Be(8);
    }
}
