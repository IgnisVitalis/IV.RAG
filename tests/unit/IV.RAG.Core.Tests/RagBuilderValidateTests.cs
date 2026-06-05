using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace IV.RAG.Tests;

public class RagBuilderValidateTests
{
    [Fact]
    public void Validate_FullToolkitMissingProviders_ThrowsListingEachMissing()
    {
        var builder = new ServiceCollection().AddRagToolkit().AddSentenceChunker(); // chunker only

        var act = () => builder.Validate();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("IEmbedder").And.Contain("IVectorStore")
                .And.Contain("IRetriever").And.Contain("IGenerator");
    }

    [Fact]
    public void Validate_CompleteConfiguration_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var builder = services.AddRagToolkit().AddSentenceChunker();
        services.AddSingleton<IEmbedder>(Substitute.For<IEmbedder>());
        services.AddSingleton<IVectorStore>(Substitute.For<IVectorStore>());
        services.AddSingleton<IRetriever>(Substitute.For<IRetriever>());
        services.AddSingleton<IGenerator>(Substitute.For<IGenerator>());

        var act = () => builder.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ChunkerPresentEmbedderMissing_DoesNotReportChunker()
    {
        var services = new ServiceCollection();
        var builder = services.AddRagToolkit().AddSentenceChunker();
        services.AddSingleton<IVectorStore>(Substitute.For<IVectorStore>());
        services.AddSingleton<IRetriever>(Substitute.For<IRetriever>());
        services.AddSingleton<IGenerator>(Substitute.For<IGenerator>());

        var act = () => builder.Validate();

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().Contain("IEmbedder");
        ex.Message.Should().NotContain("IChunker");
    }
}
