using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace IV.RAG.Tests;

public class AnswerWithSourcesTests
{
    private static readonly Document.Origin Origin = new(Guid.NewGuid(), "Test", "doc");

    private static SearchResult Result(string id) => new(new Chunk { Id = id, Text = id, Origin = Origin }, 0.9f);

    private static (IRetrievalPipeline Retrieval, IGenerator Generator) Mocks(IReadOnlyList<SearchResult> sources, string answer)
    {
        var retrieval = Substitute.For<IRetrievalPipeline>();
        retrieval.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>()).Returns(sources);
        var generator = Substitute.For<IGenerator>();
        generator.GenerateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SearchResult>>(), Arg.Any<CancellationToken>()).Returns(answer);
        return (retrieval, generator);
    }

    [Fact]
    public async Task AnswerPipeline_AnswerWithSourcesAsync_ReturnsTextAndRetrievedChunks()
    {
        var sources = new[] { Result("a"), Result("b") };
        var (retrieval, generator) = Mocks(sources, "the answer");
        var pipeline = new AnswerPipeline(retrieval, generator, NullLogger<AnswerPipeline>.Instance);

        var result = await pipeline.AnswerWithSourcesAsync("q");

        result.Text.Should().Be("the answer");
        result.Sources.Should().BeEquivalentTo(sources);
    }

    [Fact]
    public async Task AnswerPipeline_AnswerAsync_ReturnsOnlyText()
    {
        var (retrieval, generator) = Mocks(new[] { Result("a") }, "answer");
        var pipeline = new AnswerPipeline(retrieval, generator, NullLogger<AnswerPipeline>.Instance);

        (await pipeline.AnswerAsync("q")).Should().Be("answer");
    }

    [Fact]
    public async Task RagPipeline_AnswerWithSourcesAsync_ReturnsTextAndSources()
    {
        var sources = new[] { Result("a") };
        var (retrieval, generator) = Mocks(sources, "answer");
        var pipeline = new RagPipeline(Substitute.For<IIngestionPipeline>(), retrieval, generator, NullLogger<RagPipeline>.Instance);

        var result = await pipeline.AnswerWithSourcesAsync("q");

        result.Text.Should().Be("answer");
        result.Sources.Should().BeEquivalentTo(sources);
    }

    // Implements only AnswerAsync — exercises the AnswerWithSourcesAsync default interface method.
    private sealed class TextOnlyAnswerPipeline : IAnswerPipeline
    {
        public Task<string> AnswerAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult("text only");
    }

    [Fact]
    public async Task AnswerWithSourcesAsync_DefaultImplementation_ReturnsTextWithEmptySources()
    {
        IAnswerPipeline pipeline = new TextOnlyAnswerPipeline();

        var result = await pipeline.AnswerWithSourcesAsync("q");

        result.Text.Should().Be("text only");
        result.Sources.Should().BeEmpty();
    }
}
