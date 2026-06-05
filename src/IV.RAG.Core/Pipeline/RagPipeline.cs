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
