using FluentAssertions;

namespace IV.RAG.Tests;

/// <summary>
/// Verifies the default <see cref="IEmbedder.EmbedAsync(IReadOnlyList{string}, CancellationToken)"/>
/// interface method: a provider that implements only the scalar overload still gets a working batch
/// call that delegates sequentially and preserves order.
/// </summary>
public class EmbedderBatchDefaultTests
{
    private sealed class ScalarOnlyEmbedder : IEmbedder
    {
        public int ScalarCalls { get; private set; }
        public EmbedderInfo ModelInfo => new("fake", "scalar-only", 1);

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            ScalarCalls++;
            return Task.FromResult(new float[] { text.Length }); // encode input so order is verifiable
        }
    }

    [Fact]
    public async Task DefaultBatch_DelegatesToScalar_PreservingOrder()
    {
        var impl = new ScalarOnlyEmbedder();
        IEmbedder embedder = impl; // the default method is only reachable through the interface

        var result = await embedder.EmbedAsync(new[] { "a", "bb", "ccc" });

        result.Should().HaveCount(3);
        result[0].Should().Equal(1f);
        result[1].Should().Equal(2f);
        result[2].Should().Equal(3f);
        impl.ScalarCalls.Should().Be(3);
    }

    [Fact]
    public async Task DefaultBatch_EmptyInput_ReturnsEmpty()
    {
        IEmbedder embedder = new ScalarOnlyEmbedder();

        var result = await embedder.EmbedAsync(Array.Empty<string>());

        result.Should().BeEmpty();
    }
}
