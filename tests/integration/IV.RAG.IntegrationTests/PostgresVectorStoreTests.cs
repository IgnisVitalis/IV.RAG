using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.Options;

namespace IV.RAG.IntegrationTests;

public sealed class PostgresVectorStoreTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] Embedding = [1f, 0f, 0f];

    private static readonly Document.Origin TestOrigin =
        new(new Guid("a0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    public PostgresVectorStoreTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private (PostgresVectorStore Store, PostgresRetriever Retriever) Create(string tableName)
    {
        var options = Options.Create(new PostgresOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = tableName
        });
        var embedder = new FakeEmbedder(_ => Embedding, dimensions: 3);
        return (new PostgresVectorStore(_fixture.DataSource, embedder, options),
                new PostgresRetriever(_fixture.DataSource, embedder, options));
    }

    [Fact]
    public async Task SetAsync_StoresChunks_RetrievableAfterSet()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var chunk = new Chunk { Id = "1", Text = "cats", Embedding = Embedding, Origin = TestOrigin };

        await store.SetAsync(TestOrigin, [chunk]);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 1 });
        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("1");
        results[0].Chunk.Text.Should().Be("cats");
    }

    [Fact]
    public async Task SetAsync_ReIngest_RemovesStaleChunks()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());

        await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "1", Text = "old-a", Embedding = Embedding, Origin = TestOrigin, ChunkIndex = 0 },
            new Chunk { Id = "2", Text = "old-b", Embedding = Embedding, Origin = TestOrigin, ChunkIndex = 1 },
            new Chunk { Id = "3", Text = "old-c", Embedding = Embedding, Origin = TestOrigin, ChunkIndex = 2 }
        ]);
        await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "4", Text = "only-chunk", Embedding = Embedding, Origin = TestOrigin, ChunkIndex = 0 }
        ]);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });
        results.Should().HaveCount(1);
        results[0].Chunk.Text.Should().Be("only-chunk");
    }

    [Fact]
    public async Task SetAsync_PreservesChunksForOtherDocuments()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var otherOrigin = new Document.Origin(new Guid("c0000000-0000-0000-0000-000000000001"), "Test", "other-doc");

        await store.SetAsync(otherOrigin, [new Chunk { Id = "other-1", Text = "other-chunk", Embedding = Embedding, Origin = otherOrigin }]);
        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "my-chunk", Embedding = Embedding, Origin = TestOrigin, ChunkIndex = 0 }]);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });
        results.Should().HaveCount(2);
        results.Select(r => r.Chunk.Id).Should().Contain("other-1");
    }

    [Fact]
    public async Task SetAsync_EmptyChunks_DeletesAllChunksForDocument()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());

        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "chunk", Embedding = Embedding, Origin = TestOrigin }]);
        await store.SetAsync(TestOrigin, []);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAsync_ChunkWithMetadata_MetadataRoundTrips()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var metadata = new Metadata { ["source"] = "doc.txt", ["page"] = 1 };
        var chunk = new Chunk { Id = "1", Text = "text", Embedding = Embedding, Metadata = metadata, Origin = TestOrigin };

        await store.SetAsync(TestOrigin, [chunk]);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 1 });
        results[0].Chunk.Metadata.Should().ContainKey("source");
    }

    [Fact]
    public async Task SetAsync_ChunkOriginMismatch_ThrowsBeforeAnyWrite()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var wrongOrigin = new Document.Origin(new Guid("d0000000-0000-0000-0000-000000000001"), "Test", "wrong-doc");

        await store.SetAsync(TestOrigin, [new Chunk { Id = "existing", Text = "existing", Embedding = Embedding, Origin = TestOrigin }]);

        var act = async () => await store.SetAsync(TestOrigin,
        [
            new Chunk { Id = "1", Text = "ok",  Embedding = Embedding, Origin = TestOrigin },
            new Chunk { Id = "2", Text = "bad", Embedding = Embedding, Origin = wrongOrigin }
        ]);

        await act.Should().ThrowAsync<ArgumentException>();
        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });
        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("existing");
    }

    [Fact]
    public async Task SetAsync_NullChunkId_Throws()
    {
        var (store, _) = Create(PostgresContainerFixture.NewTable());

        var act = async () => await store.SetAsync(TestOrigin,
            [new Chunk { Id = null, Text = "text", Embedding = Embedding, Origin = TestOrigin }]);

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
        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "cats", Embedding = Embedding, Origin = TestOrigin }]);

        await store.DeleteAsync(["1"]);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10 });
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_DoesNotThrow()
    {
        var (store, _) = Create(PostgresContainerFixture.NewTable());
        await store.SetAsync(TestOrigin, [new Chunk { Id = "1", Text = "x", Embedding = Embedding, Origin = TestOrigin }]);

        var act = async () => await store.DeleteAsync(["unknown-id"]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteByDocumentAsync_RemovesAllChunksForDocument()
    {
        var (store, retriever) = Create(PostgresContainerFixture.NewTable());
        var origin = new Document.Origin(new Guid("b0000000-0000-0000-0000-000000000001"), "Invoice", "inv-42");
        var otherOrigin = new Document.Origin(new Guid("b0000000-0000-0000-0000-000000000001"), "Invoice", "inv-99");

        await store.SetAsync(origin,
        [
            new Chunk { Id = "1", Text = "chunk-a", Embedding = Embedding, Origin = origin },
            new Chunk { Id = "2", Text = "chunk-b", Embedding = Embedding, Origin = origin }
        ]);
        await store.SetAsync(otherOrigin,
        [
            new Chunk { Id = "3", Text = "other", Embedding = Embedding, Origin = otherOrigin }
        ]);

        await store.DeleteByDocumentAsync(origin);

        var results = await retriever.RetrieveAsync("query", new RetrievalOptions { TopK = 10, MinScore = -1f });
        results.Should().HaveCount(1);
        results[0].Chunk.Id.Should().Be("3");
    }
}
