using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

/// <summary>
/// Verifies <see cref="HybridRetrievalPipeline"/> against real PostgreSQL.
///
/// Test data uses 3-dimensional unit vectors with known cosine similarities so
/// vector retrieval order is predictable without a real embedding model.
///
/// Seed:
///   "cats are domestic animals"        → [1, 0, 0]  — strong semantic match for cat queries
///   "dogs are loyal companion animals" → [0.9, 0.436, 0]  — moderate semantic match
///   "python cats keyword document"     → [0, 0, 1]  — poor vector match for [1,0,0],
///                                                      but contains the keyword "cats"
/// </summary>
public sealed class HybridRetrievalPipelineIntegrationTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] VectorCats   = [1f, 0f, 0f];
    private static readonly float[] VectorDogs   = [0.9f, 0.436f, 0f];
    private static readonly float[] VectorKeyword = [0f, 0f, 1f];   // orthogonal to VectorCats

    private static readonly Document.Origin TestOrigin =
        new(new Guid("c0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    public HybridRetrievalPipelineIntegrationTests(PostgresContainerFixture fixture) => _fixture = fixture;

    // Builds store, vector retriever and lexical retriever sharing the same table.
    // embedder maps query strings to fixed vectors for deterministic retrieval.
    private (PostgresVectorStore Store, HybridRetrievalPipeline Pipeline) Create(
        string tableName, FakeEmbedder embedder)
    {
        var options = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName,
            VectorDimension = 3
        });
        var store = new PostgresVectorStore(_fixture.DataSource, options);
        var vectorRetriever = new PostgresRetriever(_fixture.DataSource, embedder, options);
        var lexicalRetriever = new PostgresLexicalRetriever(_fixture.DataSource, options);
        var pipeline = new HybridRetrievalPipeline(vectorRetriever, lexicalRetriever,
            logger: NullLogger<HybridRetrievalPipeline>.Instance);
        return (store, pipeline);
    }

    private static Chunk Chunk(string id, string text, float[] embedding, Metadata? metadata = null) =>
        new() { Id = id, Text = text, Embedding = embedding, Origin = TestOrigin, Metadata = metadata };

    private async Task SeedDefaultAsync(PostgresVectorStore store)
    {
        await store.SetAsync(TestOrigin,
        [
            Chunk("cats",    "cats are domestic animals",        VectorCats),
            Chunk("dogs",    "dogs are loyal companion animals", VectorDogs),
            Chunk("keyword", "python cats keyword document",     VectorKeyword)
        ]);
    }

    // ─── RRF boost ────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_ChunkInBothLists_RanksHigherThanChunkInOnlyOne()
    {
        // "cats" query: vector=[1,0,0] → vector retriever returns cats(1.0), dogs(0.9)
        //               lexical "cats" → lexical retriever returns cats, keyword (both have "cats")
        // cats appears in BOTH lists → RRF score higher than dogs (vector only)
        var embedder = FakeEmbedder.FromDictionary(new() { ["cats"] = VectorCats });
        var (store, pipeline) = Create(PostgresContainerFixture.NewTable(), embedder);
        await SeedDefaultAsync(store);

        var results = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });

        var catsRank  = results.ToList().FindIndex(r => r.Chunk.Id == "cats");
        var dogsRank  = results.ToList().FindIndex(r => r.Chunk.Id == "dogs");

        catsRank.Should().BeLessThan(dogsRank, "cats appears in both vector and lexical results");
    }

    // ─── lexical rescue ───────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_LexicalOnlyMatch_IncludedInHybridResults()
    {
        // Vector query [1,0,0] with MinScore=0: "keyword" chunk (vector=[0,0,1]) has
        // cosine_sim=0 and is excluded by vector search. Lexical "cats" finds it because
        // its text contains "cats". Hybrid search rescues it.
        var embedder = FakeEmbedder.FromDictionary(new() { ["cats"] = VectorCats });
        var (store, pipeline) = Create(PostgresContainerFixture.NewTable(), embedder);
        await SeedDefaultAsync(store);

        var results = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = 0f });

        results.Should().Contain(r => r.Chunk.Id == "keyword",
            "lexical search rescues the chunk even though its vector similarity is 0");
    }

    [Fact]
    public async Task QueryAsync_VectorOnlyMatch_IncludedInHybridResults()
    {
        // dogs chunk has no keyword match for "cats" but has good vector similarity.
        // Hybrid should include it via vector retrieval.
        var embedder = FakeEmbedder.FromDictionary(new() { ["cats"] = VectorCats });
        var (store, pipeline) = Create(PostgresContainerFixture.NewTable(), embedder);
        await SeedDefaultAsync(store);

        var results = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = 0f });

        results.Should().Contain(r => r.Chunk.Id == "dogs",
            "vector search contributes chunks with good semantic similarity");
    }

    // ─── compared to vector-only ──────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_ReturnsMoreRelevantResults_ThanVectorAlone()
    {
        // With MinScore=0 a pure vector retriever returns only cats and dogs.
        // Hybrid also returns "keyword" (rescued by lexical). Total should be 3.
        var embedder = FakeEmbedder.FromDictionary(new() { ["cats"] = VectorCats });
        var (store, pipeline) = Create(PostgresContainerFixture.NewTable(), embedder);
        await SeedDefaultAsync(store);

        var hybrid = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 10, MinScore = 0f });

        hybrid.Should().HaveCount(3, "hybrid finds cats+dogs via vector and keyword via lexical");
    }

    // ─── TopK and options ─────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_RespectsTopK()
    {
        var embedder = FakeEmbedder.FromDictionary(new() { ["cats"] = VectorCats });
        var (store, pipeline) = Create(PostgresContainerFixture.NewTable(), embedder);
        await SeedDefaultAsync(store);

        var results = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 2, MinScore = -1f });

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_NullOptions_UsesDefaults()
    {
        var embedder = FakeEmbedder.FromDictionary(new() { ["cats"] = VectorCats });
        var (store, pipeline) = Create(PostgresContainerFixture.NewTable(), embedder);
        await SeedDefaultAsync(store);

        var act = async () => await pipeline.QueryAsync("cats");

        await act.Should().NotThrowAsync();
    }

    // ─── metadata filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_MetadataFilter_AppliedToBothRetrievers()
    {
        var embedder = FakeEmbedder.FromDictionary(new() { ["cats"] = VectorCats });
        var (store, pipeline) = Create(PostgresContainerFixture.NewTable(), embedder);
        await store.SetAsync(TestOrigin,
        [
            Chunk("cats",    "cats are domestic animals",    VectorCats,    new Metadata { ["type"] = "animal" }),
            Chunk("keyword", "python cats keyword document", VectorKeyword, new Metadata { ["type"] = "software" })
        ]);

        var results = await pipeline.QueryAsync("cats",
            new RetrievalOptions
            {
                TopK = 10,
                MinScore = -1f,
                MetadataFilter = MetadataFilter.Eq("type", "animal")
            });

        results.Should().ContainSingle(r => r.Chunk.Id == "cats");
        results.Should().NotContain(r => r.Chunk.Id == "keyword");
    }

    // ─── result fields ────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryAsync_ReturnedChunks_HaveCorrectFields()
    {
        var embedder = FakeEmbedder.FromDictionary(new() { ["cats"] = VectorCats });
        var (store, pipeline) = Create(PostgresContainerFixture.NewTable(), embedder);
        await store.SetAsync(TestOrigin,
        [
            new Chunk
            {
                Id = "cats", Text = "cats are domestic animals",
                Embedding = VectorCats, Origin = TestOrigin, ChunkIndex = 2
            }
        ]);

        var results = await pipeline.QueryAsync("cats", new RetrievalOptions { TopK = 5, MinScore = -1f });

        var r = results.First(r => r.Chunk.Id == "cats");
        r.Chunk.Text.Should().Be("cats are domestic animals");
        r.Chunk.Origin.Should().Be(TestOrigin);
        r.Chunk.ChunkIndex.Should().Be(2);
        r.Score.Should().BeGreaterThan(0f);
    }
}
