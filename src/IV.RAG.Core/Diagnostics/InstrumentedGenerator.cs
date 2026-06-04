using System.Runtime.CompilerServices;

namespace IV.RAG;

/// <summary>
/// Decorates an <see cref="IGenerator"/> with an <c>rag.generate</c> span around generation.
/// Wired by <c>AddRagObservability()</c>.
/// </summary>
internal sealed class InstrumentedGenerator : IGenerator
{
    private readonly IGenerator _inner;

    public InstrumentedGenerator(IGenerator inner) => _inner = inner;

    public async Task<string> GenerateAsync(string query, IReadOnlyList<SearchResult> chunks, CancellationToken cancellationToken = default)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.generate");
        return await _inner.GenerateAsync(query, chunks, cancellationToken);
    }

    // Overridden so streaming is preserved rather than collapsing to the default single-fragment method.
    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string query,
        IReadOnlyList<SearchResult> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.generate");
        activity?.SetTag("rag.streaming", true);
        await foreach (var fragment in _inner.GenerateStreamAsync(query, chunks, cancellationToken))
            yield return fragment;
    }
}
