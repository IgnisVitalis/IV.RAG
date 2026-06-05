using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using IV.RAG.IntegrationTests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace IV.RAG.IntegrationTests;

public sealed class MultiStoreTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    private static readonly float[] Vector = [1f, 0f, 0f];

    public MultiStoreTests(PostgresContainerFixture fixture) => _fixture = fixture;

    private ServiceProvider BuildProvider(string tableA, string tableB, bool withPipelines = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbedder>(new FakeEmbedder(_ => Vector, dimensions: 3));
        var builder = new RAGBuilder(services);
        if (withPipelines) builder.AddSentenceChunker();
        builder.AddPostgresVectorStore("a", o => { o.ConnectionString = _fixture.ConnectionString; o.TableName = tableA; });
        builder.AddPostgresVectorStore("b", o => { o.ConnectionString = _fixture.ConnectionString; o.TableName = tableB; });
        if (withPipelines)
        {
            builder.AddKeyedRetrievalPipeline("a");
            builder.AddKeyedRetrievalPipeline("b");
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task KeyedStores_AreIsolatedByTable()
    {
        await using var sp = BuildProvider(PostgresContainerFixture.NewTable(), PostgresContainerFixture.NewTable());

        var storeA = sp.GetRequiredKeyedService<IVectorStore>("a");
        var storeB = sp.GetRequiredKeyedService<IVectorStore>("b");
        var retrieverA = sp.GetRequiredKeyedService<IRetriever>("a");
        var retrieverB = sp.GetRequiredKeyedService<IRetriever>("b");

        var originA = new Document.Origin(Guid.NewGuid(), "A", "doc");
        var originB = new Document.Origin(Guid.NewGuid(), "B", "doc");
        await storeA.SetAsync(originA, [new Chunk { Id = "a1", Text = "alpha", Embedding = Vector, Origin = originA }]);
        await storeB.SetAsync(originB, [new Chunk { Id = "b1", Text = "beta", Embedding = Vector, Origin = originB }]);

        var fromA = await retrieverA.RetrieveAsync("q", new RetrievalOptions { TopK = 10, MinScore = -1f });
        var fromB = await retrieverB.RetrieveAsync("q", new RetrievalOptions { TopK = 10, MinScore = -1f });

        fromA.Should().ContainSingle().Which.Chunk.Id.Should().Be("a1");
        fromB.Should().ContainSingle().Which.Chunk.Id.Should().Be("b1");
    }

    [Fact]
    public async Task KeyedRetrievalPipelines_IngestAndQuery_TheirOwnStore()
    {
        await using var sp = BuildProvider(PostgresContainerFixture.NewTable(), PostgresContainerFixture.NewTable(), withPipelines: true);

        var ingestA = sp.GetRequiredKeyedService<IIngestionPipeline>("a");
        var ingestB = sp.GetRequiredKeyedService<IIngestionPipeline>("b");
        await ingestA.IngestAsync(new PlainTextDocument { Source = new Document.Origin(Guid.NewGuid(), "A", "doc-a"), Text = "alpha document." });
        await ingestB.IngestAsync(new PlainTextDocument { Source = new Document.Origin(Guid.NewGuid(), "B", "doc-b"), Text = "beta document." });

        var fromA = await sp.GetRequiredKeyedService<IRetrievalPipeline>("a").QueryAsync("q", new RetrievalOptions { TopK = 10, MinScore = -1f });
        var fromB = await sp.GetRequiredKeyedService<IRetrievalPipeline>("b").QueryAsync("q", new RetrievalOptions { TopK = 10, MinScore = -1f });

        fromA.Should().ContainSingle().Which.Chunk.Text.Should().Contain("alpha");
        fromB.Should().ContainSingle().Which.Chunk.Text.Should().Contain("beta");
    }
}
