using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

/// <summary>
/// Uses 3-dimensional unit vectors with known cosine similarities.
/// </summary>
public sealed class PostgresQueryCacheTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] VecA = [1f, 0f, 0f];
    private static readonly float[] VecB = [0.9f, 0.436f, 0f]; // cosine ≈ 0.9 to VecA
    private static readonly float[] VecC = [0f, 1f, 0f];       // cosine = 0.0 to VecA

    private static readonly Document.Origin Origin1 =
        new(new Guid("a0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    private static readonly Document.Origin Origin2 =
        new(new Guid("b0000000-0000-0000-0000-000000000002"), "Test", "doc-2");

    private static readonly RetrievalOptions DefaultOptions = new();

    public PostgresQueryCacheTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private PostgresQueryCache CreateCache(string tableName, float threshold = 0.95f, TimeSpan? ttl = null) =>
        new(_fixture.DataSource,
            Options.Create(new PostgresOptions
            {
                ConnectionString = _fixture.ConnectionString,
                VectorDimension = 3,
                QueryCacheTableName = tableName
            }),
            Options.Create(new QueryCacheOptions
            {
                SimilarityThreshold = threshold,
                Ttl = ttl ?? TimeSpan.FromHours(1)
            }));

    private static IReadOnlyList<SearchResult> Results(Document.Origin origin, string chunkId = "c1") =>
        [new SearchResult(new Chunk { Id = chunkId, Text = "text", Origin = origin }, 0.9f)];

    [Fact]
    public async Task GetAsync_EmptyTable_ReturnsNull()
    {
        var cache = CreateCache(PostgresContainerFixture.NewTable());
        var result = await cache.GetAsync(VecA, DefaultOptions);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_IdenticalEmbedding_ReturnsHit()
    {
        var cache = CreateCache(PostgresContainerFixture.NewTable());
        var stored = Results(Origin1);
        await cache.SetAsync(VecA, DefaultOptions, stored);

        var result = await cache.GetAsync(VecA, DefaultOptions);

        result.Should().NotBeNull();
        result!.Should().HaveCount(1);
        result[0].Chunk.Id.Should().Be("c1");
    }

    [Fact]
    public async Task GetAsync_SimilarEmbedding_AboveThreshold_ReturnsHit()
    {
        var cache = CreateCache(PostgresContainerFixture.NewTable(), threshold: 0.85f);
        await cache.SetAsync(VecA, DefaultOptions, Results(Origin1));

        // VecB has cosine ≈ 0.9 to VecA, above threshold 0.85
        var result = await cache.GetAsync(VecB, DefaultOptions);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_DissimilarEmbedding_BelowThreshold_ReturnsNull()
    {
        var cache = CreateCache(PostgresContainerFixture.NewTable(), threshold: 0.95f);
        await cache.SetAsync(VecA, DefaultOptions, Results(Origin1));

        // VecC has cosine = 0.0 to VecA, below threshold 0.95
        var result = await cache.GetAsync(VecC, DefaultOptions);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_DifferentOptions_ReturnsNull()
    {
        var cache = CreateCache(PostgresContainerFixture.NewTable());
        await cache.SetAsync(VecA, new RetrievalOptions { TopK = 5 }, Results(Origin1));

        var result = await cache.GetAsync(VecA, new RetrievalOptions { TopK = 10 });

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ExpiredEntry_ReturnsNull()
    {
        var cache = CreateCache(PostgresContainerFixture.NewTable(), ttl: TimeSpan.FromMilliseconds(1));
        await cache.SetAsync(VecA, DefaultOptions, Results(Origin1));
        await Task.Delay(50);

        var result = await cache.GetAsync(VecA, DefaultOptions);

        result.Should().BeNull();
    }

    [Fact]
    public async Task InvalidateByDocumentAsync_RemovesMatchingEntries()
    {
        var table = PostgresContainerFixture.NewTable();
        var cache = CreateCache(table);
        await cache.SetAsync(VecA, DefaultOptions, Results(Origin1, "c-origin1"));
        await cache.SetAsync(VecC, DefaultOptions, Results(Origin2, "c-origin2"));

        await cache.InvalidateByDocumentAsync(Origin1);

        (await cache.GetAsync(VecA, DefaultOptions)).Should().BeNull();
        (await cache.GetAsync(VecC, DefaultOptions)).Should().NotBeNull();
    }

    [Fact]
    public async Task SetAsync_ExpiredEntriesAreCleanedUp_OnNextSet()
    {
        var table = PostgresContainerFixture.NewTable();

        // Insert with a short TTL, then wait for it to expire
        var shortTtlCache = CreateCache(table, ttl: TimeSpan.FromMilliseconds(1));
        await shortTtlCache.SetAsync(VecA, DefaultOptions, Results(Origin1, "first"));
        await Task.Delay(50);

        // Insert a fresh entry with a normal TTL — SetAsync also deletes expired rows
        var normalCache = CreateCache(table, ttl: TimeSpan.FromHours(1));
        await normalCache.SetAsync(VecC, DefaultOptions, Results(Origin1, "second"));

        (await normalCache.GetAsync(VecA, DefaultOptions)).Should().BeNull();
        (await normalCache.GetAsync(VecC, DefaultOptions)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_RountripsChunkFields()
    {
        var cache = CreateCache(PostgresContainerFixture.NewTable());
        var chunk = new Chunk
        {
            Id = "chunk-42",
            Text = "hello world",
            Origin = Origin1,
            ChunkIndex = 3,
            Metadata = new Metadata { ["lang"] = "en" }
        };
        var stored = new List<SearchResult> { new(chunk, 0.88f) };
        await cache.SetAsync(VecA, DefaultOptions, stored);

        var result = (await cache.GetAsync(VecA, DefaultOptions))!;

        result.Should().HaveCount(1);
        var r = result[0];
        r.Score.Should().BeApproximately(0.88f, 0.001f);
        r.Chunk.Id.Should().Be("chunk-42");
        r.Chunk.Text.Should().Be("hello world");
        r.Chunk.ChunkIndex.Should().Be(3);
        r.Chunk.Origin.Should().Be(Origin1);
        r.Chunk.Metadata!["lang"].Should().Be((MetadataFilterValue)"en");
    }
}
