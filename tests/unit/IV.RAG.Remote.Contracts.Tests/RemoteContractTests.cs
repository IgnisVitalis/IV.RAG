using FluentAssertions;

namespace IV.RAG.Tests;

public sealed class RemoteContractTests
{
    private static readonly Document.Origin Origin = new(Guid.NewGuid(), "Invoice", "INV-1");

    [Fact]
    public void SearchResult_RoundTripsThroughDto()
    {
        var original = new SearchResult(
            new Chunk
            {
                Id = "c1",
                Text = "hello",
                ChunkIndex = 3,
                Origin = Origin,
                Metadata = new Metadata { ["k"] = "v", ["n"] = 1 }
            },
            0.87f);

        var roundTripped = original.ToDto().ToSearchResult();

        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void NullableFields_RoundTrip()
    {
        var original = new SearchResult(
            new Chunk { Id = null, Text = "t", ChunkIndex = null, Origin = Origin, Metadata = null }, 0f);

        var roundTripped = original.ToDto().ToSearchResult();

        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void QueryRequest_RoundTripsRetrievalOptions()
    {
        var filter = MetadataFilter.Eq("dept", "eng");
        var options = new RetrievalOptions { TopK = 7, MinScore = 0.3f, MetadataFilter = filter };

        var request = RemoteContract.ToQueryRequest("q", options);
        var roundTripped = request.ToRetrievalOptions();

        request.Query.Should().Be("q");
        roundTripped.TopK.Should().Be(7);
        roundTripped.MinScore.Should().Be(0.3f);
        roundTripped.MetadataFilter.Should().BeSameAs(filter);
    }

    [Fact]
    public void QueryResponse_RoundTripsResultList()
    {
        IReadOnlyList<SearchResult> results =
        [
            new(new Chunk { Id = "a", Text = "A", Origin = Origin }, 0.9f),
            new(new Chunk { Id = "b", Text = "B", Origin = Origin }, 0.5f)
        ];

        var roundTripped = results.ToQueryResponse().ToSearchResults();

        roundTripped.Should().BeEquivalentTo(results);
    }
}
