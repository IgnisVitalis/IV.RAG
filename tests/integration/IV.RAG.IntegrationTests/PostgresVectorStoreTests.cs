using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IV.RAG.IntegrationTests;

public sealed class PostgresVectorStoreTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly Document.Origin TestOrigin =
        new(new Guid("a0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

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
    public async Task SetAsync_StoresChunks_RetrievableAfterSet()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        var chunk = new Chunk { Id = "1", Text = "cats", Embedding = embedding, Origin = TestOrigin };

        await store.SetAsync(TestOrigin, [chunk]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 1 });
        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("1");
        results[0].Chunk.Text.Should().Be("cats");
    }

    [Fact]
    public async Task SetAsync_ReIngest_RemovesStaleChunks()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };

        await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "1", Text = "old-a", Embedding = embedding, Origin = TestOrigin, ChunkIndex = 0 },
            new Chunk { Id = "2", Text = "old-b", Embedding = embedding, Origin = TestOrigin, ChunkIndex = 1 },
            new Chunk { Id = "3", Text = "old-c", Embedding = embedding, Origin = TestOrigin, ChunkIndex = 2 }
        ]);
        await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "4", Text = "only-chunk", Embedding = embedding, Origin = TestOrigin, ChunkIndex = 0 }
        ]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 10 });
        results.Should().HaveCount(1);
        results[0].Chunk.Text.Should().Be("only-chunk");
    }

    [Fact]
    public async Task SetAsync_PreservesChunksForOtherDocuments()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        var otherOrigin = new Document.Origin(new Guid("c0000000-0000-0000-0000-000000000001"), "Test", "other-doc");

        await store.SetAsync(otherOrigin, [new Chunk { Id = "other-1", Text = "other-chunk", Embedding = embedding, Origin = otherOrigin }]);
        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "my-chunk", Embedding = embedding, Origin = TestOrigin, ChunkIndex = 0 }]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 10 });
        results.Should().HaveCount(2);
        results.Select(r => r.Chunk.Id).Should().Contain("other-1");
    }

    [Fact]
    public async Task SetAsync_EmptyChunks_DeletesAllChunksForDocument()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };

        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "chunk", Embedding = embedding, Origin = TestOrigin }]);
        await store.SetAsync(TestOrigin, []);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 10 });
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAsync_ChunkWithMetadata_MetadataRoundTrips()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        var metadata = new Dictionary<string, object> { ["source"] = "doc.txt", ["page"] = 1 };
        var chunk = new Chunk { Id = "1", Text = "text", Embedding = embedding, Metadata = metadata, Origin = TestOrigin };

        await store.SetAsync(TestOrigin, [chunk]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 1 });
        results[0].Chunk.Metadata.Should().ContainKey("source");
    }

    [Fact]
    public async Task SetAsync_ChunkOriginMismatch_ThrowsBeforeAnyWrite()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        var wrongOrigin = new Document.Origin(new Guid("d0000000-0000-0000-0000-000000000001"), "Test", "wrong-doc");

        await store.SetAsync(TestOrigin, [new Chunk { Id = "existing", Text = "existing", Embedding = embedding, Origin = TestOrigin }]);

        var act = async () => await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "1", Text = "ok",  Embedding = embedding, Origin = TestOrigin },
            new Chunk { Id = "2", Text = "bad", Embedding = embedding, Origin = wrongOrigin }
        ]);

        await act.Should().ThrowAsync<ArgumentException>();
        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 10 });
        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("existing");
    }

    [Fact]
    public async Task SetAsync_NullChunkId_Throws()
    {
        var (store, _) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };

        var act = async () => await store.SetAsync(TestOrigin,
            [new Chunk { Id = null, Text = "text", Embedding = embedding, Origin = TestOrigin }]);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetAsync_NullEmbedding_Throws()
    {
        var (store, _) = Create(PostgresContainerFixture.NewTable());

        var act = async () => await store.SetAsync(TestOrigin,
            [new Chunk { Id = "1", Text = "text", Embedding = null, Origin = TestOrigin }]);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteAsync_RemovesChunk()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "cats", Embedding = embedding, Origin = TestOrigin }]);

        await store.DeleteAsync(["1"]);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 10 });
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_DoesNotThrow()
    {
        var (store, _) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "x", Embedding = new float[] { 1f, 0f, 0f }, Origin = TestOrigin }]);

        var act = async () => await store.DeleteAsync(["unknown-id"]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteByDocumentAsync_RemovesAllChunksForDocument()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var embedding = new float[] { 1f, 0f, 0f };
        var origin = new Document.Origin(new Guid("b0000000-0000-0000-0000-000000000001"), "Invoice", "inv-42");
        var otherOrigin = new Document.Origin(new Guid("b0000000-0000-0000-0000-000000000001"), "Invoice", "inv-99");

        await store.SetAsync(origin,
        [
            new Chunk { Id = "1", Text = "chunk-a", Embedding = embedding, Origin = origin },
            new Chunk { Id = "2", Text = "chunk-b", Embedding = embedding, Origin = origin }
        ]);
        await store.SetAsync(otherOrigin,
        [
            new Chunk { Id = "3", Text = "other", Embedding = embedding, Origin = otherOrigin }
        ]);

        await store.DeleteByDocumentAsync(origin);

        var results = await retriever.RetrieveAsync(embedding, new RetrievalOptions { TopK = 10, MinScore = -1f });
        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("3");
    }
}
