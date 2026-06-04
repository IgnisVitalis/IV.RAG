using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IV.RAG.Tests;

public sealed class OllamaConfigValidationTests
{
    private static IOptions<OllamaOptions> ResolveEmbedderOptions(string endpoint)
    {
        var services = new ServiceCollection();
        new RAGBuilder(services).AddOllamaEmbedder(o => o.Endpoint = endpoint);
        return services.BuildServiceProvider().GetRequiredService<IOptions<OllamaOptions>>();
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("localhost")]
    [InlineData("")]
    public void AddOllamaEmbedder_InvalidEndpoint_FailsValidation(string endpoint)
    {
        var options = ResolveEmbedderOptions(endpoint);

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddOllamaGenerator_ValidEndpoint_PassesValidation()
    {
        var services = new ServiceCollection();
        new RAGBuilder(services).AddOllamaGenerator(o => o.Endpoint = "http://localhost:11434");
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<OllamaOptions>>();

        var act = () => options.Value;

        act.Should().NotThrow();
    }
}
