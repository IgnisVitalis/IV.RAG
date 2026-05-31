namespace IV.RagToolkit;

/// <summary>Persists and manages <see cref="Chunk"/> records with their embeddings.</summary>
public interface IVectorStore
{
    /// <summary>Inserts or updates <paramref name="chunks"/>. Each chunk must have a non-null <see cref="Chunk.Id"/> and <see cref="Chunk.Embedding"/>.</summary>
    Task UpsertAsync(IEnumerable<Chunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Removes chunks by their identifiers. Silently ignores unknown ids.</summary>
    Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Removes all chunks belonging to the document identified by <paramref name="origin"/>.</summary>
    Task DeleteByDocumentAsync(Document.Origin origin, CancellationToken cancellationToken = default);
}
