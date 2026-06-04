namespace IV.RAG;

/// <summary>
/// Decorates an <see cref="IEmbedder"/> with an <c>rag.embed</c> span and the <c>rag.embed_calls</c>
/// counter around each call. Wired by <c>AddRagObservability()</c>, so every embed — ingestion and
/// retrieval alike — is captured regardless of the underlying provider.
/// </summary>
internal sealed class InstrumentedEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;

    public InstrumentedEmbedder(IEmbedder inner) => _inner = inner;

    public EmbedderInfo ModelInfo => _inner.ModelInfo;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.embed");
        RagDiagnostics.EmbedCalls.Add(1);
        return await _inner.EmbedAsync(text, cancellationToken);
    }

    // Overridden (not left to the default interface method) so batching is preserved: the default
    // would loop the scalar overload, defeating the provider's batch call.
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.embed");
        activity?.SetTag("rag.batch_size", texts.Count);
        RagDiagnostics.EmbedCalls.Add(1);
        return await _inner.EmbedAsync(texts, cancellationToken);
    }
}
