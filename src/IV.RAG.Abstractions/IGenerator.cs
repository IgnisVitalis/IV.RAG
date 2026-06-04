using System.Runtime.CompilerServices;

namespace IV.RAG;

/// <summary>Generates a natural language answer from a query and retrieved context chunks.</summary>
public interface IGenerator
{
    /// <summary>Generates an answer to <paramref name="query"/> using <paramref name="chunks"/> as context.</summary>
    Task<string> GenerateAsync(string query, IReadOnlyList<SearchResult> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the answer to <paramref name="query"/> as it is produced, yielding incremental text
    /// fragments in order. The default implementation calls <see cref="GenerateAsync"/> and yields the
    /// whole answer as a single fragment; providers that support token streaming should override it.
    /// </summary>
    async IAsyncEnumerable<string> GenerateStreamAsync(
        string query,
        IReadOnlyList<SearchResult> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await GenerateAsync(query, chunks, cancellationToken);
    }
}
