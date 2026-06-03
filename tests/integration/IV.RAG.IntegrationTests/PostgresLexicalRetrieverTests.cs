using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

/// <summary>
/// Verifies <see cref="PostgresLexicalRetriever"/> against a real PostgreSQL instance.
/// Each test gets an isolated table. Embeddings are dummy values — only text content matters here.
/// </summary>
public sealed class PostgresLexicalRetrieverTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    // Dummy unit vectors — the lexical retriever ignores embeddings, but the vector
    // store requires them to be non-null to satisfy the schema NOT NULL constraint.
    private static readonly float[] AnyVector = [1f, 0f, 0f];

    private static readonly Document.Origin Origin =
        new(new Guid("b0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    public PostgresLexicalRetrieverTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private (PostgresVectorStore Store, PostgresLexicalRetriever Retriever) Create(string tableName)
    {
        var options = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName,
            VectorDimension = 3
        });
        return (new PostgresVectorStore(_fixture.DataSource, options),
                new PostgresLexicalRetriever(_fixture.DataSource, options));
    }

    private static Chunk MakeChunk(string id, string text, Metadata? metadata = null) =>
        new() { Id = id, Text = text, Embedding = AnyVector, Origin = Origin, Metadata = metadata };

    // ─── basic matching ───────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_QueryMatchesText_ReturnsChunk()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(Origin, [MakeChunk("1", "cats are domestic animals")]);

        var results = await retriever.RetrieveAsync("animals", new RetrievalOptions { TopK = 10 });

        results.Should().ContainSingle(r => r.Chunk.Id == "1");
    }

    [Fact]
    public async Task RetrieveAsync_QueryDoesNotMatch_ReturnsEmpty()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(Origin, [MakeChunk("1", "cats are domestic animals")]);

        var results = await retriever.RetrieveAsync("quantum physics", new RetrievalOptions { TopK = 10 });

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_EmptyStore_ReturnsEmpty()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(Origin, []); // schema only

        var results = await retriever.RetrieveAsync("animals", new RetrievalOptions { TopK = 10 });

        results.Should().BeEmpty();
    }

    // ─── ranking ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_OrderedByRelevance_MoreTermOccurrencesRanksFirst()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(Origin,
        [
            // Both contain "dogs" and "animals"; "high" has both terms more frequently.
            MakeChunk("low",  "dogs are animals"),
            MakeChunk("high", "dogs are loyal animals and dogs are companion animals too")
        ]);

        var results = await retriever.RetrieveAsync("dogs animals", new RetrievalOptions { TopK = 10 });

        results.Should().HaveCount(2);
        results[0].Chunk.Id.Should().Be("high");
        results[0].Score.Should().BeGreaterThan(results[1].Score);
    }

    [Fact]
    public async Task RetrieveAsync_RespectsTopK()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(Origin,
        [
            MakeChunk("1", "cats are domestic animals"),
            MakeChunk("2", "dogs are loyal animals"),
            MakeChunk("3", "birds are flying animals")
        ]);

        var results = await retriever.RetrieveAsync("animals", new RetrievalOptions { TopK = 2 });

        results.Should().HaveCount(2);
    }

    // ─── stemming ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_Stemming_QueryWordRootMatchesStoredWord()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(Origin, [MakeChunk("1", "she was running quickly through the park")]);

        // "run" should match "running" via the English stemmer
        var results = await retriever.RetrieveAsync("run", new RetrievalOptions { TopK = 10 });

        results.Should().ContainSingle(r => r.Chunk.Id == "1");
    }

    [Fact]
    public async Task RetrieveAsync_Stemming_PluralMatchesSingular()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(Origin, [MakeChunk("1", "the cat sat on the mat")]);

        // "cats" (plural) should match "cat" (singular) via stemming
        var results = await retriever.RetrieveAsync("cats", new RetrievalOptions { TopK = 10 });

        results.Should().ContainSingle(r => r.Chunk.Id == "1");
    }

    // ─── metadata filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_MetadataFilter_NarrowsResults()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(Origin,
        [
            MakeChunk("animal", "cats are domestic animals",     new Metadata { ["type"] = "animal" }),
            MakeChunk("code",   "python for scripting animals",  new Metadata { ["type"] = "software" })
        ]);

        var results = await retriever.RetrieveAsync("animals",
            new RetrievalOptions
            {
                TopK = 10,
                MetadataFilter = MetadataFilter.Eq("type", "animal")
            });

        results.Should().ContainSingle(r => r.Chunk.Id == "animal");
    }

    // ─── returned fields ──────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_RoundTripsAllChunkFields()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var metadata = new Metadata { ["source"] = "test.txt", ["page"] = 3 };
        var chunk = MakeChunk("id-1", "cats are domestic animals", metadata) with { ChunkIndex = 7 };
        await store.SetAsync(Origin, [chunk]);

        var results = await retriever.RetrieveAsync("animals", new RetrievalOptions { TopK = 1 });

        var r = results.Single();
        r.Chunk.Id.Should().Be("id-1");
        r.Chunk.Text.Should().Be("cats are domestic animals");
        r.Chunk.Origin.Should().Be(Origin);
        r.Chunk.ChunkIndex.Should().Be(7);
        r.Chunk.Metadata!["source"].Should().Be(new MetadataFilterValue.Text("test.txt"));
        r.Chunk.Metadata!["page"].Should().Be(new MetadataFilterValue.Number(3));
        r.Score.Should().BeGreaterThan(0f);
    }
}
