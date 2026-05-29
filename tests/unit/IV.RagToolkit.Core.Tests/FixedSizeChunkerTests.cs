using FluentAssertions;
using Microsoft.Extensions.Options;

namespace IV.RagToolkit.Tests;

public class FixedSizeChunkerTests
{
    private static FixedSizeChunker Create(int chunkSize = 10, int overlap = 0) =>
        new(Options.Create(new FixedSizeChunkerOptions { ChunkSize = chunkSize, Overlap = overlap }));

    [Fact]
    public async Task ChunkAsync_TextShorterThanChunkSize_ReturnsSingleChunk()
    {
        var chunker = Create(chunkSize: 100);

        var chunks = await chunker.ChunkAsync(new Document("short")).ToListAsync();

        chunks.Should().HaveCount(1);
        chunks[0].Text.Should().Be("short");
    }

    [Fact]
    public async Task ChunkAsync_TextExactlyChunkSize_ReturnsSingleChunk()
    {
        var chunker = Create(chunkSize: 5);
        var text = "12345";

        var chunks = await chunker.ChunkAsync(new Document(text)).ToListAsync();

        chunks.Should().HaveCount(1);
        chunks[0].Text.Should().Be(text);
    }

    [Fact]
    public async Task ChunkAsync_NoOverlap_ProducesNonOverlappingChunks()
    {
        var chunker = Create(chunkSize: 5, overlap: 0);

        var chunks = await chunker.ChunkAsync(new Document("1234567890")).ToListAsync();

        chunks.Should().HaveCount(2);
        chunks[0].Text.Should().Be("12345");
        chunks[1].Text.Should().Be("67890");
    }

    [Fact]
    public async Task ChunkAsync_WithOverlap_ConsecutiveChunksShareCharacters()
    {
        // step = 5 - 2 = 3 → positions 0, 3, 6
        var chunker = Create(chunkSize: 5, overlap: 2);

        var chunks = await chunker.ChunkAsync(new Document("1234567890")).ToListAsync();

        chunks[0].Text.Should().Be("12345");
        chunks[1].Text.Should().Be("45678"); // shares "45" with previous
        chunks[2].Text.Should().Be("7890");  // last chunk shorter
    }

    [Fact]
    public async Task ChunkAsync_LastChunk_SmallerWhenTextNotDivisible()
    {
        var chunker = Create(chunkSize: 4, overlap: 0);

        var chunks = await chunker.ChunkAsync(new Document("123456789")).ToListAsync();

        chunks.Last().Text.Should().Be("9");
    }

    [Fact]
    public async Task ChunkAsync_PropagatesDocumentMetadata()
    {
        var chunker = Create(chunkSize: 100);
        var metadata = new Dictionary<string, object> { ["source"] = "doc.txt" };

        var chunks = await chunker.ChunkAsync(new Document("text", metadata)).ToListAsync();

        chunks[0].Metadata.Should().BeEquivalentTo(metadata);
    }

    [Fact]
    public async Task ChunkAsync_ChunksHaveNoIdOrEmbedding()
    {
        var chunker = Create(chunkSize: 100);

        var chunks = await chunker.ChunkAsync(new Document("text")).ToListAsync();

        chunks[0].Id.Should().BeNull();
        chunks[0].Embedding.Should().BeNull();
    }

    [Fact]
    public void Constructor_OverlapEqualToChunkSize_Throws()
    {
        var act = () => Create(chunkSize: 5, overlap: 5);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Constructor_OverlapGreaterThanChunkSize_Throws()
    {
        var act = () => Create(chunkSize: 5, overlap: 6);

        act.Should().Throw<InvalidOperationException>();
    }
}
