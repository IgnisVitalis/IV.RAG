using System.Text;
using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.Options;
using Pgvector;

namespace IV.RAG.IntegrationTests;

public sealed class PostgresVectorIndexTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] QueryVector = [1f, 0f, 0f];

    private static readonly Document.Origin TestOrigin =
        new(new Guid("e0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    public PostgresVectorIndexTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private (PostgresVectorStore Store, PostgresRetriever Retriever) Create(
        string tableName, Action<PostgresOptions>? configure = null, int dimensions = 3)
    {
        var opts = new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = tableName };
        configure?.Invoke(opts);
        var options = Options.Create(opts);
        var embedder = new FakeEmbedder(_ => QueryVector, dimensions: dimensions);
        return (new PostgresVectorStore(_fixture.DataSource, embedder, options),
                new PostgresRetriever(_fixture.DataSource, embedder, options));
    }

    private async Task<string?> GetIndexDefAsync(string tableName)
    {
        await using var cmd = _fixture.DataSource.CreateCommand();
        cmd.CommandText = """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = current_schema() AND indexname = @indexName
            """;
        cmd.Parameters.AddWithValue("indexName", $"{tableName}_embedding_idx");
        return (string?)await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task EnsureSchema_DefaultOptions_CreatesHnswCosineIndex()
    {
        var table = PostgresContainerFixture.NewTable();
        var (store, _) = Create(table);

        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "x", Embedding = QueryVector, Origin = TestOrigin }]);

        var indexDef = await GetIndexDefAsync(table);
        indexDef.Should().NotBeNull();
        indexDef.Should().Contain("hnsw").And.Contain("vector_cosine_ops");
    }

    [Fact]
    public async Task EnsureSchema_IVFFlat_CreatesIvfflatIndex()
    {
        var table = PostgresContainerFixture.NewTable();
        var (store, _) = Create(table, o => { o.VectorIndex = VectorIndexType.IVFFlat; o.IVFFlatLists = 1; });

        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "x", Embedding = QueryVector, Origin = TestOrigin }]);

        var indexDef = await GetIndexDefAsync(table);
        indexDef.Should().NotBeNull();
        indexDef.Should().Contain("ivfflat").And.Contain("vector_cosine_ops");
    }

    [Fact]
    public async Task EnsureSchema_VectorIndexNone_CreatesNoVectorIndex()
    {
        var table = PostgresContainerFixture.NewTable();
        var (store, _) = Create(table, o => o.VectorIndex = VectorIndexType.None);

        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "x", Embedding = QueryVector, Origin = TestOrigin }]);

        (await GetIndexDefAsync(table)).Should().BeNull();
    }

    [Fact]
    public async Task Retrieval_WithHnswIndex_UsesIndexScan()
    {
        var table = PostgresContainerFixture.NewTable();
        var (store, retriever) = Create(table);

        // Enough rows that, with sequential scans disabled, the planner must use the ANN index.
        var chunks = new List<Chunk>();
        for (var i = 0; i < 100; i++)
        {
            var angle = i * (2.0 * Math.PI / 100);
            float[] embedding = [(float)Math.Cos(angle), (float)Math.Sin(angle), 0.1f];
            chunks.Add(new Chunk { Id = i.ToString(), Text = $"chunk {i}", Embedding = embedding, Origin = TestOrigin });
        }
        await store.SetAsync(TestOrigin, chunks);

        var plan = await ExplainRetrievalAsync(table);

        plan.Should().Contain("Index Scan").And.Contain($"{table}_embedding_idx");

        // Sanity: retrieval still returns results with the index present.
        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 5, MinScore = -1f });
        results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EnsureSchema_DimensionChange_RecreatesIndexAtNewDimension()
    {
        var table = PostgresContainerFixture.NewTable();
        var (store3d, _) = Create(table, dimensions: 3);
        await store3d.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "x", Embedding = QueryVector, Origin = TestOrigin }]);

        // A new embedder with a different dimension triggers the column-retype path, which drops
        // the vector index before the ALTER and recreates it afterward at the new dimension.
        var (store4d, _) = Create(table, dimensions: 4);
        var act = async () => await store4d.SetAsync(TestOrigin, []);
        await act.Should().ThrowAsync<EmbeddingModelMismatchException>();

        var indexDef = await GetIndexDefAsync(table);
        indexDef.Should().NotBeNull();
        indexDef.Should().Contain("hnsw");

        await using var cmd = _fixture.DataSource.CreateCommand();
        cmd.CommandText = """
            SELECT pa.atttypmod FROM pg_attribute pa
            WHERE pa.attrelid = to_regclass(@tableName) AND pa.attname = 'embedding'
              AND pa.attnum > 0 AND NOT pa.attisdropped
            """;
        cmd.Parameters.AddWithValue("tableName", table);
        ((int)(await cmd.ExecuteScalarAsync())!).Should().Be(4);
    }

    private async Task<string> ExplainRetrievalAsync(string table)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();

        await using (var off = conn.CreateCommand())
        {
            off.CommandText = "SET enable_seqscan = off";
            await off.ExecuteNonQueryAsync();
        }

        await using var explain = conn.CreateCommand();
        explain.CommandText = $"""
            EXPLAIN SELECT id FROM {table}
            ORDER BY embedding <=> @embedding
            LIMIT 5
            """;
        explain.Parameters.AddWithValue("embedding", new Vector(QueryVector));

        var plan = new StringBuilder();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            plan.AppendLine(reader.GetString(0));
        return plan.ToString();
    }
}
