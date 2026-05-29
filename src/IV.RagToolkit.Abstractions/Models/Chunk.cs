namespace IV.RagToolkit;

/// <summary>
/// The unit of currency in the RAG pipeline.
/// Produced by <see cref="IChunker"/>, enriched with an embedding by <see cref="IEmbedder"/>,
/// stored by <see cref="IVectorStore"/>, and returned by <see cref="IRetriever"/>.
/// </summary>
public sealed record Chunk
{
    /// <summary>Text content of this chunk.</summary>
    public required string Text { get; init; }

    /// <summary>Stable identifier assigned before storage. Null until the pipeline sets it.</summary>
    public string? Id { get; init; }

    /// <summary>Vector embedding. Null until <see cref="IEmbedder"/> processes the chunk.</summary>
    public float[]? Embedding { get; init; }

    /// <summary>Arbitrary metadata propagated from the source <see cref="Document"/>.</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
