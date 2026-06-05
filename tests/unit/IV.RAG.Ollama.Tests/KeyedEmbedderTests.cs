using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace IV.RAG.Tests;

public class KeyedEmbedderTests
{
    [Fact]
    public void AddOllamaEmbedder_Keyed_RegistersDistinctEmbedderPerKey()
    {
        var services = new ServiceCollection();
        var builder = new RAGBuilder(services);
        builder.AddOllamaEmbedder("fast", o => { o.Endpoint = "http://localhost:11434"; o.EmbeddingModel = "nomic-embed-text"; });
        builder.AddOllamaEmbedder("quality", o => { o.Endpoint = "http://localhost:11434"; o.EmbeddingModel = "mxbai-embed-large"; });
        using var provider = services.BuildServiceProvider();

        var fast = provider.GetRequiredKeyedService<IEmbedder>("fast");
        var quality = provider.GetRequiredKeyedService<IEmbedder>("quality");

        fast.Should().NotBeSameAs(quality);
        fast.ModelInfo.ModelName.Should().Be("nomic-embed-text");
        quality.ModelInfo.ModelName.Should().Be("mxbai-embed-large");
    }

    [Fact]
    public void AddOllamaEmbedder_Keyed_DoesNotRegisterDefaultEmbedder()
    {
        var services = new ServiceCollection();
        new RAGBuilder(services).AddOllamaEmbedder("only", o => o.Endpoint = "http://localhost:11434");
        using var provider = services.BuildServiceProvider();

        provider.GetService<IEmbedder>().Should().BeNull();               // no unkeyed registration
        provider.GetKeyedService<IEmbedder>("only").Should().NotBeNull(); // keyed one is present
    }
}
