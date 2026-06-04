using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace IV.RAG.Tests;

public class StreamingGenerationTests
{
    private static readonly Document.Origin Origin = new(Guid.NewGuid(), "Test", "doc");

    // Implements only the scalar overload — exercises the GenerateStreamAsync default interface method.
    private sealed class NonStreamingGenerator : IGenerator
    {
        public Task<string> GenerateAsync(string query, IReadOnlyList<SearchResult> chunks, CancellationToken cancellationToken = default)
            => Task.FromResult($"answer to {query}");
    }

    // Overrides streaming with real token fragments.
    private sealed class TokenGenerator : IGenerator
    {
        public Task<string> GenerateAsync(string query, IReadOnlyList<SearchResult> chunks, CancellationToken cancellationToken = default)
            => Task.FromResult("AB");

        public async IAsyncEnumerable<string> GenerateStreamAsync(
            string query, IReadOnlyList<SearchResult> chunks,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return "A";
            yield return "B";
        }
    }

    [Fact]
    public async Task GenerateStreamAsync_DefaultImplementation_YieldsWholeAnswerAsOneFragment()
    {
        IGenerator generator = new NonStreamingGenerator();

        var fragments = new List<string>();
        await foreach (var fragment in generator.GenerateStreamAsync("q", []))
            fragments.Add(fragment);

        fragments.Should().ContainSingle().Which.Should().Be("answer to q");
    }

    [Fact]
    public async Task AnswerPipeline_AnswerStreamAsync_RetrievesThenStreamsGeneratorFragments()
    {
        var retrieval = Substitute.For<IRetrievalPipeline>();
        retrieval.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new SearchResult(new Chunk { Text = "x", Origin = Origin }, 0.9f) });
        var pipeline = new AnswerPipeline(retrieval, new TokenGenerator(), NullLogger<AnswerPipeline>.Instance);

        var fragments = new List<string>();
        await foreach (var fragment in pipeline.AnswerStreamAsync("q"))
            fragments.Add(fragment);

        fragments.Should().Equal("A", "B");
        await retrieval.Received(1).QueryAsync("q", Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RagPipeline_AnswerStreamAsync_StreamsGeneratorFragments()
    {
        var retrieval = Substitute.For<IRetrievalPipeline>();
        retrieval.QueryAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchResult>());
        var pipeline = new RagPipeline(
            Substitute.For<IIngestionPipeline>(), retrieval, new TokenGenerator(), NullLogger<RagPipeline>.Instance);

        var fragments = new List<string>();
        await foreach (var fragment in pipeline.AnswerStreamAsync("q"))
            fragments.Add(fragment);

        fragments.Should().Equal("A", "B");
    }
}
