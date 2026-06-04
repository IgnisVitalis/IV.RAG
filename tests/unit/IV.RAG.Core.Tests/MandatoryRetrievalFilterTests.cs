using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace IV.RAG.Tests;

public class MandatoryRetrievalFilterTests
{
    private static (IRetrievalPipeline Pipeline, IRetrievalPipeline Inner) Build(Func<IServiceProvider, MetadataFilter?> factory)
    {
        var inner = Substitute.For<IRetrievalPipeline>();
        inner.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());

        var services = new ServiceCollection();
        services.AddSingleton(inner);
        new RAGBuilder(services).AddMandatoryRetrievalFilter(factory);
        var pipeline = services.BuildServiceProvider().GetRequiredService<IRetrievalPipeline>();
        return (pipeline, inner);
    }

    [Fact]
    public async Task NoCallerFilter_AppliesRequiredFilter()
    {
        var required = MetadataFilter.Eq("tenant", "A");
        var (pipeline, inner) = Build(_ => required);

        await pipeline.QueryAsync("q");

        await inner.Received(1).QueryAsync("q",
            Arg.Is<RetrievalOptions?>(o => ReferenceEquals(o!.MetadataFilter, required)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallerFilter_IsCombinedWithRequiredFilter()
    {
        var required = MetadataFilter.Eq("tenant", "A");
        var callerFilter = MetadataFilter.Eq("year", 2024);
        var (pipeline, inner) = Build(_ => required);

        await pipeline.QueryAsync("q", new RetrievalOptions { MetadataFilter = callerFilter });

        // Combined into a new filter — neither the caller's nor the required one alone.
        await inner.Received(1).QueryAsync("q",
            Arg.Is<RetrievalOptions?>(o => o!.MetadataFilter != null
                && !ReferenceEquals(o.MetadataFilter, required)
                && !ReferenceEquals(o.MetadataFilter, callerFilter)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FactoryReturnsNull_PassesOptionsThroughUnchanged()
    {
        var (pipeline, inner) = Build(_ => null);
        var options = new RetrievalOptions { MetadataFilter = MetadataFilter.Eq("year", 2024) };

        await pipeline.QueryAsync("q", options);

        await inner.Received(1).QueryAsync("q",
            Arg.Is<RetrievalOptions?>(o => ReferenceEquals(o, options)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Throws_WhenNoRetrievalPipelineRegistered()
    {
        var services = new ServiceCollection();

        var act = () => new RAGBuilder(services).AddMandatoryRetrievalFilter(_ => null);

        act.Should().Throw<InvalidOperationException>();
    }
}
