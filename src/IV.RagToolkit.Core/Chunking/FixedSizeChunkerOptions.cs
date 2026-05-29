namespace IV.RagToolkit;

/// <summary>Options for <see cref="FixedSizeChunker"/>.</summary>
public sealed class FixedSizeChunkerOptions
{
    /// <summary>Maximum number of characters per chunk. Defaults to 512.</summary>
    public int ChunkSize { get; init; } = 512;

    /// <summary>
    /// Number of characters shared between consecutive chunks.
    /// Helps preserve context at chunk boundaries. Defaults to 50.
    /// Must be less than <see cref="ChunkSize"/>.
    /// </summary>
    public int Overlap { get; init; } = 50;
}
