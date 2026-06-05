using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace IV.RAG.Tests;

public class OllamaEmbedderWarmupTests
{
    private static IHostedService BuildWarmup(IEmbedder embedder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(embedder);
        new RAGBuilder(services).AddOllamaEmbedderWarmup();
        return services.BuildServiceProvider().GetRequiredService<IHostedService>();
    }

    [Fact]
    public async Task Warmup_ProbesEmbedderAtStartup()
    {
        var embedder = Substitute.For<IEmbedder>();
        embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f });
        var warmup = BuildWarmup(embedder);

        await warmup.StartAsync(CancellationToken.None);

        await embedder.Received(1).EmbedAsync("warmup", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Warmup_Failure_IsNonFatal()
    {
        var embedder = Substitute.For<IEmbedder>();
        embedder.When(e => e.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new HttpRequestException("ollama down"));
        var warmup = BuildWarmup(embedder);

        var act = async () => await warmup.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
