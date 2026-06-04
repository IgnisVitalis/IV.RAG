using System.Runtime.CompilerServices;

namespace IV.RAG;

/// <summary>Handles the full answer loop: retrieve relevant chunks, then generate a natural language answer.</summary>
public interface IAnswerPipeline
{
    /// <summary>Retrieves relevant chunks for <paramref name="query"/> and returns a generated answer.</summary>
    Task<string> AnswerAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves relevant chunks for <paramref name="query"/> and returns the generated answer along
    /// with the source chunks it was grounded in. The default implementation calls
    /// <see cref="AnswerAsync"/> and returns an empty source list; implementations that retrieve
    /// internally should override it to populate the sources.
    /// </summary>
    async Task<AnswerResult> AnswerWithSourcesAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
        new(await AnswerAsync(query, options, cancellationToken), []);

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
