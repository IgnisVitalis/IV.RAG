using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace IV.RAG;

/// <summary>
/// Full local RAG pipeline. Delegates ingestion and retrieval to <see cref="IIngestionPipeline"/>
/// and <see cref="IRetrievalPipeline"/>, and generation to <see cref="IGenerator"/>.
/// </summary>
public sealed class RagPipeline : IRagPipeline
{
    private readonly IIngestionPipeline _ingestion;
    private readonly IRetrievalPipeline _retrieval;
    private readonly IGenerator _generator;
    private readonly ILogger<RagPipeline> _logger;

    /// <summary>Initializes a new instance with all required pipeline components.</summary>
    public RagPipeline(
        IIngestionPipeline ingestion,
        IRetrievalPipeline retrieval,
        IGenerator generator,
        ILogger<RagPipeline> logger)
    {
        _ingestion = ingestion;
        _retrieval = retrieval;
        _generator = generator;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task IngestAsync(Document document, CancellationToken cancellationToken = default)
        => _ingestion.IngestAsync(document, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchResult>> QueryAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => _retrieval.QueryAsync(query, options, cancellationToken);

    /// <inheritdoc/>
    public async Task<string> AnswerAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default) =>
        (await AnswerWithSourcesAsync(query, options, cancellationToken)).Text;

    /// <inheritdoc/>
    public async Task<AnswerResult> AnswerWithSourcesAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.answer");
        _logger.LogDebug("Answering: \"{Query}\".", query);

        var chunks = await _retrieval.QueryAsync(query, options, cancellationToken);
        var answer = await _generator.GenerateAsync(query, chunks, cancellationToken);

        _logger.LogDebug("Generated answer ({Length} chars).", answer.Length);
        return new AnswerResult(answer, chunks);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> AnswerStreamAsync(
        string query,
        RetrievalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = RagDiagnostics.ActivitySource.StartActivity("rag.answer");
        activity?.SetTag("rag.streaming", true);
        _logger.LogDebug("Answering (streaming): \"{Query}\".", query);

        var chunks = await _retrieval.QueryAsync(query, options, cancellationToken);
        await foreach (var fragment in _generator.GenerateStreamAsync(query, chunks, cancellationToken))
            yield return fragment;
    }
}
