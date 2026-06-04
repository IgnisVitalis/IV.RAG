using System.Runtime.CompilerServices;

namespace IV.RAG;

/// <summary>Handles the full answer loop: retrieve relevant chunks, then generate a natural language answer.</summary>
public interface IAnswerPipeline
{
    /// <summary>Retrieves relevant chunks for <paramref name="query"/> and returns a generated answer.</summary>
    Task<string> AnswerAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves relevant chunks for <paramref name="query"/> and streams the generated answer as
    /// incremental text fragments. The default implementation calls <see cref="AnswerAsync"/> and
    /// yields the whole answer as a single fragment; implementations backed by a streaming generator
    /// should override it.
    /// </summary>
    async IAsyncEnumerable<string> AnswerStreamAsync(
        string query,
        RetrievalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await AnswerAsync(query, options, cancellationToken);
    }
}
