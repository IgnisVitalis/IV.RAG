using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

/// <summary>
/// Uses 3-dimensional unit vectors with known cosine similarities:
///   "cats" → [1, 0, 0]
///   "dogs" → [0.9, 0.436, 0]   cosine_sim to "cats" ≈ 0.9
///   "cars" → [0, 1, 0]         cosine_sim to "cats" = 0.0
/// </summary>
public sealed class PostgresRetrieverTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] VectorCats = [1f, 0f, 0f];
    private static readonly float[] VectorDogs = [0.9f, 0.436f, 0f];
    private static readonly float[] VectorCars = [0f, 1f, 0f];

    private static readonly Document.Origin TestOrigin =
        new(new Guid("a0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    public PostgresRetrieverTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private IOptions<PostgresOptions> TableOptions(string tableName) =>
        Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName
        });

    private PostgresRetriever CreateRetriever(string tableName, float[] queryVector) =>
        new(_fixture.DataSource, new FakeEmbedder(_ => queryVector, dimensions: 3), TableOptions(tableName));

    private async Task SeedAsync(string tableName)
    {
        var embedder = new FakeEmbedder(_ => VectorCats, dimensions: 3);
        var store = new PostgresVectorStore(_fixture.DataSource, embedder, TableOptions(tableName));
        await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "cats", Text = "cats are animals", Embedding = VectorCats, Origin = TestOrigin },
            new Chunk { Id = "dogs", Text = "dogs are animals", Embedding = VectorDogs, Origin = TestOrigin },
            new Chunk { Id = "cars", Text = "cars are vehicles", Embedding = VectorCars, Origin = TestOrigin }
        ]);
    }

    [Fact]
    public async Task RetrieveByVectorAsync_MatchesRetrieveAsync_ForSameQueryVector()
    {
        var table = PostgresContainerFixture.NewTable();
        await SeedAsync(table);
        var retriever = CreateRetriever(table, VectorCats); // FakeEmbedder maps any text → VectorCats
        var options = new RetrievalOptions { TopK = 3, MinScore = -1f };

        var viaString = await retriever.RetrieveAsync("query", options);
        var viaVector = await retriever.RetrieveByVectorAsync(VectorCats, options);

        viaVector.Should().BeEquivalentTo(viaString);
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsSimilarChunksInDescendingOrder()
    {
        var table = PostgresContainerFixture.NewTable();
        await SeedAsync(table);
        var retriever = CreateRetriever(table, VectorCats);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 3, MinScore = -1f });

        results.Should().HaveCount(3);
        results[0].Chunk.Id.Should().Be("cats");
        results[1].Chunk.Id.Should().Be("dogs");
        results[2].Chunk.Id.Should().Be("cars");
        results[0].Score.Should().BeGreaterThan(results[1].Score);
        results[1].Score.Should().BeGreaterThan(results[2].Score);
    }

    [Fact]
    public async Task RetrieveAsync_RespectsTopK()
    {
        var table = PostgresContainerFixture.NewTable();
        await SeedAsync(table);
        var retriever = CreateRetriever(table, VectorCats);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 2, MinScore = -1f });

        results.Should().HaveCount(2);
        results[0].Chunk.Id.Should().Be("cats");
        results[1].Chunk.Id.Should().Be("dogs");
    }

    [Fact]
    public async Task RetrieveAsync_FiltersChunksBelowMinScore()
    {
        var table = PostgresContainerFixture.NewTable();
        await SeedAsync(table);
        var retriever = CreateRetriever(table, VectorCats);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10, MinScore = 0.5f });

        results.Should().HaveCount(2);
        results.Should().NotContain(r => r.Chunk.Id == "cars");
    }

    [Fact]
    public async Task RetrieveAsync_DefaultMinScore_ExcludesOrthogonalChunks()
    {
        var table = PostgresContainerFixture.NewTable();
        await SeedAsync(table);
        var retriever = CreateRetriever(table, VectorCats);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });

        results.Should().NotContain(r => r.Chunk.Id == "cars");
        results.Should().OnlyContain(r => r.Score > 0.0f);
    }

    [Fact]
    public async Task RetrieveAsync_ScoreIsWithinValidRange()
    {
        var table = PostgresContainerFixture.NewTable();
        await SeedAsync(table);
        var retriever = CreateRetriever(table, VectorCats);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10, MinScore = -1f });

        results.Should().OnlyContain(r => r.Score >= -1f && r.Score <= 1f);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyStore_ReturnsEmpty()
    {
        var table = PostgresContainerFixture.NewTable();
        var store = new PostgresVectorStore(_fixture.DataSource, new FakeEmbedder(_ => VectorCats, 3), TableOptions(table));
        await store.SetAsync(TestOrigin, []); // trigger schema creation
        var retriever = CreateRetriever(table, VectorCats);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });

        results.Should().BeEmpty();
    }

    // ── metadata filter tests ─────────────────────────────────────────────────
    // Seed: cats → {department=animals, year=2020}
    //       dogs → {department=animals, year=2021}
    //       cars → {department=vehicles, year=2019}

    private async Task<PostgresRetriever> SeedWithMetadataAsync(string tableName)
    {
        await new PostgresVectorStore(_fixture.DataSource, new FakeEmbedder(_ => VectorCats, 3), TableOptions(tableName)).SetAsync(TestOrigin,
        [
            new Chunk
            {
                Id = "cats", Text = "cats are animals", Embedding = VectorCats, Origin = TestOrigin,
                Metadata = new Metadata { ["department"] = "animals", ["year"] = 2020 }
            },
            new Chunk
            {
                Id = "dogs", Text = "dogs are animals", Embedding = VectorDogs, Origin = TestOrigin,
                Metadata = new Metadata { ["department"] = "animals", ["year"] = 2021 }
            },
            new Chunk
            {
                Id = "cars", Text = "cars are vehicles", Embedding = VectorCars, Origin = TestOrigin,
                Metadata = new Metadata { ["department"] = "vehicles", ["year"] = 2019 }
            }
        ]);

        return CreateRetriever(tableName, VectorCats);
    }

    [Fact]
    public async Task RetrieveAsync_EqFilter_ReturnsOnlyMatchingChunks()
    {
        var retriever = await SeedWithMetadataAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync("query",
            new RetrievalOptions { TopK = 10, MinScore = -1f, MetadataFilter = MetadataFilter.Eq("department", "animals") });

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Chunk.Id == "cats" || r.Chunk.Id == "dogs");
    }

    [Fact]
    public async Task RetrieveAsync_GtFilter_ReturnsChunksAboveThreshold()
    {
        var retriever = await SeedWithMetadataAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync("query",
            new RetrievalOptions { TopK = 10, MinScore = -1f, MetadataFilter = MetadataFilter.Gt("year", 2020) });

        results.Should().ContainSingle(r => r.Chunk.Id == "dogs");
    }

    [Fact]
    public async Task RetrieveAsync_GteFilter_ReturnsChunksAtAndAboveThreshold()
    {
        var retriever = await SeedWithMetadataAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync("query",
            new RetrievalOptions { TopK = 10, MinScore = -1f, MetadataFilter = MetadataFilter.Gte("year", 2020) });

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Chunk.Id == "cats" || r.Chunk.Id == "dogs");
    }

    [Fact]
    public async Task RetrieveAsync_InFilter_ReturnsOnlyMembersOfSet()
    {
        var retriever = await SeedWithMetadataAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync("query",
            new RetrievalOptions { TopK = 10, MinScore = -1f, MetadataFilter = MetadataFilter.In("department", "animals", "vehicles") });

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task RetrieveAsync_AndFilter_CombinesTwoConditions()
    {
        var retriever = await SeedWithMetadataAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync("query",
            new RetrievalOptions
            {
                TopK = 10, MinScore = -1f,
                MetadataFilter = MetadataFilter.And(
                    MetadataFilter.Eq("department", "animals"),
                    MetadataFilter.Gte("year", 2021))
            });

        results.Should().ContainSingle(r => r.Chunk.Id == "dogs");
    }

    [Fact]
    public async Task RetrieveAsync_OrFilter_ReturnsUnionOfMatches()
    {
        var retriever = await SeedWithMetadataAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync("query",
            new RetrievalOptions
            {
                TopK = 10, MinScore = -1f,
                MetadataFilter = MetadataFilter.Or(
                    MetadataFilter.Eq("department", "vehicles"),
                    MetadataFilter.Gt("year", 2020))
            });

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Chunk.Id == "cars" || r.Chunk.Id == "dogs");
    }

    [Fact]
    public async Task RetrieveAsync_NotFilter_ExcludesMatchingChunks()
    {
        var retriever = await SeedWithMetadataAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync("query",
            new RetrievalOptions { TopK = 10, MinScore = -1f, MetadataFilter = MetadataFilter.Not(MetadataFilter.Eq("department", "animals")) });

        results.Should().ContainSingle(r => r.Chunk.Id == "cars");
    }

    [Fact]
    public async Task RetrieveAsync_MetadataFilter_RoundTripsMetadataValues()
    {
        var retriever = await SeedWithMetadataAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync("query",
            new RetrievalOptions { TopK = 1, MinScore = -1f, MetadataFilter = MetadataFilter.Eq("department", "animals") });

        var metadata = results[0].Chunk.Metadata!;
        metadata["department"].Should().Be(new MetadataFilterValue.Text("animals"));
        metadata["year"].Should().Be(new MetadataFilterValue.Number(2020));
    }
}
