using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace IV.RAG.Tests;

public class OllamaResilienceTests
{
    [Fact]
    public async Task Generator_RequestExceedsTimeout_FailsFastInsteadOfHanging()
    {
        var services = new ServiceCollection();
        new RAGBuilder(services).AddOllamaGenerator(o =>
        {
            o.Endpoint = "http://localhost:11434";
            o.GenerationTimeoutSeconds = 1;
        });
        // Replace the primary handler with one that stalls far longer than the timeout.
        services.AddHttpClient("IV.RAG.Ollama.Generator")
            .ConfigurePrimaryHttpMessageHandler(() => new StallingHandler(TimeSpan.FromSeconds(30)));

        await using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<IGenerator>();

        var stopwatch = Stopwatch.StartNew();
        var act = async () => await generator.GenerateAsync("question", []);

        await act.Should().ThrowAsync<Exception>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10)); // timed out at ~1s, did not hang 30s
    }

    [Fact]
    public void AddOllamaGenerator_DefaultTimeouts_BuildsValidResiliencePipeline()
    {
        var services = new ServiceCollection();
        new RAGBuilder(services).AddOllamaGenerator(o => o.Endpoint = "http://localhost:11434");
        using var provider = services.BuildServiceProvider();

        // Resolving the generator builds the client + resilience pipeline, triggering options
        // validation — the long generation timeout must not violate the handler's constraints.
        var act = () => provider.GetRequiredService<IGenerator>();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddOllamaEmbedder_DefaultTimeouts_BuildsValidResiliencePipeline()
    {
        var services = new ServiceCollection();
        new RAGBuilder(services).AddOllamaEmbedder(o => o.Endpoint = "http://localhost:11434");
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IEmbedder>();

        act.Should().NotThrow();
    }
}

internal sealed class StallingHandler : HttpMessageHandler
{
    private readonly TimeSpan _delay;

    internal StallingHandler(TimeSpan delay) => _delay = delay;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(_delay, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
    }
}
