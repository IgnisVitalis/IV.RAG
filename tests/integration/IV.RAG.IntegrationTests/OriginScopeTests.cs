using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

public sealed class OriginScopeTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] Vector = [1f, 0f, 0f];
    private static readonly Document.Origin OriginA = new(new Guid("a1000000-0000-0000-0000-000000000001"), "Invoice", "inv-1");
    private static readonly Document.Origin OriginB = new(new Guid("b2000000-0000-0000-0000-000000000002"), "Email", "email-1");

    public OriginScopeTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private IOptions<PostgresOptions> Opts(string table) =>
        Options.Create(new PostgresOptions { ConnectionString = _fixture.ConnectionString, TableName = table });

    private async Task<(PostgresRetriever Vector, PostgresLexicalRetriever Lexical)> SeedAsync(string table)
    {
        var embedder = new FakeEmbedder(_ => Vector, dimensions: 3);
        var store = new PostgresVectorStore(_fixture.DataSource, embedder, Opts(table));
        await store.SetAsync(OriginA, [new Chunk { Id = "a1", Text = "shared term alpha", Embedding = Vector, Origin = OriginA }]);
        await store.SetAsync(OriginB, [new Chunk { Id = "b1", Text = "shared term beta", Embedding = Vector, Origin = OriginB }]);
        return (new PostgresRetriever(_fixture.DataSource, embedder, Opts(table)),
                new PostgresLexicalRetriever(_fixture.DataSource, Opts(table)));
    }

    [Fact]
    public async Task Vector_NoScope_ReturnsAllOrigins()
    {
        var (vector, _) = await SeedAsync(PostgresContainerFixture.NewTable());

        var results = await vector.RetrieveAsync("query", new RetrievalOptions { TopK = 10, MinScore = -1f });

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task Vector_ScopedBySourceId_ReturnsOnlyMatchingOrigin()
    {
        var (vector, _) = await SeedAsync(PostgresContainerFixture.NewTable());

        var results = await vector.RetrieveAsync("query",
            new RetrievalOptions { TopK = 10, MinScore = -1f, SourceId = OriginA.SourceId });

        results.Should().ContainSingle().Which.Chunk.Id.Should().Be("a1");
    }

    [Fact]
    public async Task Vector_ScopedByDocumentType_ReturnsOnlyMatchingType()
    {
        var (vector, _) = await SeedAsync(PostgresContainerFixture.NewTable());

        var results = await vector.RetrieveAsync("query",
            new RetrievalOptions { TopK = 10, MinScore = -1f, DocumentType = "Email" });

        results.Should().ContainSingle().Which.Chunk.Id.Should().Be("b1");
    }

    [Fact]
    public async Task Lexical_ScopedBySourceId_ReturnsOnlyMatchingOrigin()
    {
        var (_, lexical) = await SeedAsync(PostgresContainerFixture.NewTable());

        var results = await lexical.RetrieveAsync("shared",
            new RetrievalOptions { TopK = 10, SourceId = OriginA.SourceId });

        results.Should().ContainSingle().Which.Chunk.Id.Should().Be("a1");
    }
}
