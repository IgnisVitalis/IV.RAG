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
        CancellationToken cancellationToken = default) =>
        (await AnswerWithSourcesAsync(query, options, cancellationToken)).Text;

    /// <inheritdoc/>
    public Task<AnswerResult> AnswerWithSourcesAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default) =>
        AnswerLoop.AnswerWithSourcesAsync(_retrieval, _generator, _logger, query, options, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<string> AnswerStreamAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default) =>
        AnswerLoop.AnswerStreamAsync(_retrieval, _generator, _logger, query, options, cancellationToken);
}
