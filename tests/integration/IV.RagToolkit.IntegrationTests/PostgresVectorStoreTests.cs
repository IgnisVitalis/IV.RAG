using FluentAssertions;
using IV.RagToolkit.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IV.RagToolkit.IntegrationTests;

public sealed class PostgresVectorStoreTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    public PostgresVectorStoreTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private (PostgresVectorStore Store, PostgresRetriever Retriever) Create(string tableName)
    {
        var options = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName,
            VectorDimension = 3
        });
        return (new PostgresVectorStore(_fixture.DataSource, options),
                new PostgresRetriever(_fixture.DataSource, options));
    }

    [Fact]
    public async Task UpsertAsync_StoresChunk_RetrievableAfterUpsert()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        var chunk = new Chunk { Id = "1", Text = "cats", Embedding = embedding };

        await store.UpsertAsync([chunk]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 1 });
        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("1");
        results[0].Chunk.Text.Should().Be("cats");
    }

    [Fact]
    public async Task UpsertAsync_ExistingId_UpdatesChunk()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };

        await store.UpsertAsync([new Chunk { Id = "1", Text = "original", Embedding = embedding }]);
        await store.UpsertAsync([new Chunk { Id = "1", Text = "updated", Embedding = embedding }]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 10 });
        results.Should().HaveCount(1);
        results[0].Chunk.Text.Should().Be("updated");
    }

    [Fact]
    public async Task DeleteAsync_RemovesChunk()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        await store.UpsertAsync([new Chunk { Id = "1", Text = "cats", Embedding = embedding }]);

        await store.DeleteAsync(["1"]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 10 });
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_DoesNotThrow()
    {
        var (store, _) = Create(PostgresContainerFixture.NewTable());
        await store.UpsertAsync([new Chunk { Id = "1", Text = "x", Embedding = new float[] { 1f, 0f, 0f } }]);

        var act = async () => await store.DeleteAsync(["unknown-id"]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpsertAsync_ChunkWithMetadata_MetadataRoundTrips()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        var metadata = new Dictionary<string, object> { ["source"] = "doc.txt", ["page"] = 1 };
        var chunk = new Chunk { Id = "1", Text = "text", Embedding = embedding, Metadata = metadata };

        await store.UpsertAsync([chunk]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 1 });
        results[0].Chunk.Metadata.Should().ContainKey("source");
    }
}
