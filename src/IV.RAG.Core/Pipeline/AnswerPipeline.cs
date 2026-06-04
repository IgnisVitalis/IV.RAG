using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace IV.RAG;

/// <summary>
/// Answer pipeline for clients: delegates retrieval to <see cref="IRetrievalPipeline"/>
/// and generation to <see cref="IGenerator"/>. Does not handle ingestion.
/// </summary>
public sealed class AnswerPipeline : IAnswerPipeline
{
    private readonly IRetrievalPipeline _retrieval;
    private readonly IGenerator _generator;
    private readonly ILogger<AnswerPipeline> _logger;

    /// <summary>Initializes a new instance with a retrieval pipeline and a generator.</summary>
    public AnswerPipeline(IRetrievalPipeline retrieval, IGenerator generator, ILogger<AnswerPipeline> logger)
    {
        _retrieval = retrieval;
        _generator = generator;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> AnswerAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Answering: \"{Query}\".", query);

        var chunks = await _retrieval.QueryAsync(query, options, cancellationToken);
        var answer = await _generator.GenerateAsync(query, chunks, cancellationToken);

        _logger.LogDebug("Generated answer ({Length} chars).", answer.Length);
        return answer;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> AnswerStreamAsync(
        string query,
        RetrievalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Answering (streaming): \"{Query}\".", query);

        var chunks = await _retrieval.QueryAsync(query, options, cancellationToken);
        await foreach (var fragment in _generator.GenerateStreamAsync(query, chunks, cancellationToken))
            yield return fragment;
    }
}
