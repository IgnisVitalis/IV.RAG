namespace IV.RAG;

/// <summary>Persists and manages <see cref="Chunk"/> records with their embeddings.</summary>
public interface IVectorStore
{
    /// <summary>
    /// Atomically sets the chunks for <paramref name="origin"/> to exactly <paramref name="chunks"/>.
    /// All existing chunks for the origin are removed, then <paramref name="chunks"/> are inserted in the same transaction.
    /// Passing an empty sequence removes all chunks for the origin without inserting any.
    /// Each chunk must have a non-null <see cref="Chunk.Id"/> and <see cref="Chunk.Embedding"/>,
    /// and its <see cref="Chunk.Origin"/> must equal <paramref name="origin"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Any chunk's <see cref="Chunk.Origin"/> does not equal <paramref name="origin"/>.</exception>
    Task SetAsync(Document.Origin origin, IEnumerable<Chunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Removes chunks by their identifiers. Silently ignores unknown ids.</summary>
    Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Removes all chunks belonging to the document identified by <paramref name="origin"/>.</summary>
    Task DeleteByDocumentAsync(Document.Origin origin, CancellationToken cancellationToken = default);
}
