using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace IV.RAG.Tests;

public class ObservabilityTests
{
    private static readonly Document.Origin Origin = new(Guid.NewGuid(), "Test", "doc");

    private static ActivityListener ListenFor(List<Activity> spans) => new()
    {
        ShouldListenTo = source => source.Name == RagDiagnostics.Name,
        Sample = (ref ActivityCreationOptions<ActivityContext> o) => ActivitySamplingResult.AllData,
        ActivityStopped = spans.Add
    };

    [Fact]
    public async Task AddRagObservability_DecoratesEmbedder_ForwardsCallAndCountsIt()
    {
        var inner = Substitute.For<IEmbedder>();
        inner.EmbedAsync("q", Arg.Any<CancellationToken>()).Returns(new float[] { 1f });

        var services = new ServiceCollection();
        services.AddSingleton(inner);
        new RAGBuilder(services).AddRagObservability();
        using var provider = services.BuildServiceProvider();
        var embedder = provider.GetRequiredService<IEmbedder>();

        long embedCalls = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RagDiagnostics.Name && instrument.Name == "rag.embed_calls")
                l.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref embedCalls, measurement));
        meterListener.Start();

        var result = await embedder.EmbedAsync("q");

        embedder.Should().NotBeSameAs(inner); // decorated
        result.Should().Equal(1f);            // forwarded to the inner embedder
        await inner.Received(1).EmbedAsync("q", Arg.Any<CancellationToken>());
        embedCalls.Should().Be(1);
    }

    [Fact]
    public async Task AddRagObservability_DecoratesGenerator_EmitsGenerateSpan()
    {
        var inner = Substitute.For<IGenerator>();
        inner.GenerateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>())
            .Returns("answer");

        var services = new ServiceCollection();
        services.AddSingleton(inner);
        new RAGBuilder(services).AddRagObservability();
        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<IGenerator>();

        var spans = new List<Activity>();
        using var listener = ListenFor(spans);
        ActivitySource.AddActivityListener(listener);

        var answer = await generator.GenerateAsync("q", []);

        answer.Should().Be("answer");
        spans.Select(s => s.OperationName).Should().Contain("rag.generate");
    }

    [Fact]
    public async Task RetrievalPipeline_QueryAsync_EmitsRetrieveSpan()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new SearchResult(new Chunk { Text = "x", Origin = Origin }, 0.9f) });
        var pipeline = new RetrievalPipeline(
            Substitute.For<IChunker>(), Substitute.For<IEmbedder>(), Substitute.For<IVectorStore>(),
            retriever, NullLogger<RetrievalPipeline>.Instance);

        var spans = new List<Activity>();
        using var listener = ListenFor(spans);
        ActivitySource.AddActivityListener(listener);

        await pipeline.QueryAsync("q");

        spans.Select(s => s.OperationName).Should().Contain("rag.retrieve");
    }
}
