namespace IV.RagToolkit;

/// <summary>Splits a <see cref="Document"/> into <see cref="Chunk"/> instances.</summary>
public interface IChunker
{
    /// <summary>Splits <paramref name="document"/> into chunks, yielding each as it is produced.</summary>
    IAsyncEnumerable<Chunk> ChunkAsync(Document document, CancellationToken cancellationToken = default);
}
