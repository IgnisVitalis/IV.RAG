using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
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

    private async Task<(PostgresVectorStore Store, PostgresRetriever Retriever)> CreateAndSeedAsync(string tableName)
    {
        var options = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName,
            VectorDimension = 3
        });
        var store = new PostgresVectorStore(_fixture.DataSource, options);
        var retriever = new PostgresRetriever(_fixture.DataSource, options);

        await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "cats", Text = "cats are animals", Embedding = VectorCats, Origin = TestOrigin },
            new Chunk { Id = "dogs", Text = "dogs are animals", Embedding = VectorDogs, Origin = TestOrigin },
            new Chunk { Id = "cars", Text = "cars are vehicles", Embedding = VectorCars, Origin = TestOrigin }
        ]);

        return (store, retriever);
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsSimilarChunksInDescendingOrder()
    {
        var (_, retriever) = await CreateAndSeedAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync(VectorCats, new RetrievalOptions { TopK = 3, MinScore = -1f });

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
        var (_, retriever) = await CreateAndSeedAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync(VectorCats, new RetrievalOptions { TopK = 2, MinScore = -1f });

        results.Should().HaveCount(2);
        results[0].Chunk.Id.Should().Be("cats");
        results[1].Chunk.Id.Should().Be("dogs");
    }

    [Fact]
    public async Task RetrieveAsync_FiltersChunksBelowMinScore()
    {
        var (_, retriever) = await CreateAndSeedAsync(PostgresContainerFixture.NewTable());

        // MinScore = 0.5 should exclude "cars" (score ≈ 0) and "dogs" (score ≈ 0.9 passes)
        var results = await retriever.RetrieveAsync(VectorCats, new RetrievalOptions { TopK = 10, MinScore = 0.5f });

        results.Should().HaveCount(2);
        results.Should().NotContain(r => r.Chunk.Id == "cars");
    }

    [Fact]
    public async Task RetrieveAsync_DefaultMinScore_ExcludesOrthogonalChunks()
    {
        var (_, retriever) = await CreateAndSeedAsync(PostgresContainerFixture.NewTable());

        // MinScore = 0.0 uses >, so "cars" (score exactly 0.0) is excluded
        var results = await retriever.RetrieveAsync(VectorCats, new RetrievalOptions { TopK = 10 });

        results.Should().NotContain(r => r.Chunk.Id == "cars");
        results.Should().OnlyContain(r => r.Score > 0.0f);
    }

    [Fact]
    public async Task RetrieveAsync_ScoreIsWithinValidRange()
    {
        var (_, retriever) = await CreateAndSeedAsync(PostgresContainerFixture.NewTable());

        var results = await retriever.RetrieveAsync(VectorCats, new RetrievalOptions { TopK = 10, MinScore = -1f });

        results.Should().OnlyContain(r => r.Score >= -1f && r.Score <= 1f);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyStore_ReturnsEmpty()
    {
        var options = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = PostgresContainerFixture.NewTable(),
            VectorDimension = 3
        });
        var store = new PostgresVectorStore(_fixture.DataSource, options);
        var retriever = new PostgresRetriever(_fixture.DataSource, options);
        await store.SetAsync(TestOrigin, []); // trigger schema creation

        var results = await retriever.RetrieveAsync(VectorCats, new RetrievalOptions { TopK = 10 });

        results.Should().BeEmpty();
    }
}
