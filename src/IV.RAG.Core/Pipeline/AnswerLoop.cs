using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace IV.RAG;

// The shared retrieve -> generate loop used by both AnswerPipeline and RagPipeline, so the logic
// (and its span) lives in one place.
internal static class AnswerLoop
{
    internal static async Task<AnswerResult> AnswerWithSourcesAsync(
        IRetrievalPipeline retrieval,
        IGenerator generator,
        ILogger logger,
        string query,
        RetrievalOptions? options,
        CancellationToken cancellationToken)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.answer");
        logger.LogDebug("Answering: \"{Query}\".", query);

        var chunks = await retrieval.QueryAsync(query, options, cancellationToken);
        var answer = await generator.GenerateAsync(query, chunks, cancellationToken);

        logger.LogDebug("Generated answer ({Length} chars).", answer.Length);
        return new AnswerResult(answer, chunks);
    }

    internal static async IAsyncEnumerable<string> AnswerStreamAsync(
        IRetrievalPipeline retrieval,
        IGenerator generator,
        ILogger logger,
        string query,
        RetrievalOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.answer");
        activity?.SetTag("rag.streaming", true);
        logger.LogDebug("Answering (streaming): \"{Query}\".", query);

        var chunks = await retrieval.QueryAsync(query, options, cancellationToken);
        await foreach (var fragment in generator.GenerateStreamAsync(query, chunks, cancellationToken))
            yield return fragment;
    }
}
