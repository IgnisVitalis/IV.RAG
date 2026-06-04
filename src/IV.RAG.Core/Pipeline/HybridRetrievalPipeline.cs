using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IV.RAG;

/// <summary>
/// Retrieval pipeline that combines vector and lexical search via Reciprocal Rank Fusion (RRF),
/// optionally followed by cross-encoder reranking.
/// Replaces <see cref="RetrievalPipeline"/> as the <see cref="IRetrievalPipeline"/> when
/// hybrid search is configured via <c>AddHybridRetrievalPipeline()</c>.
/// </summary>
/// <remarks>
/// <para><b>Retrieval flow:</b></para>
/// <list type="number">
/// <item>
///   Both sub-retrievers are queried in parallel with an expanded candidate count
///   (<see cref="HybridRetrievalOptions.CandidateMultiplier"/> × <c>TopK</c>).
/// </item>
/// <item>
///   Results are fused via RRF: each chunk scores <c>1 / (k + rank)</c> per list it
///   appears in, and scores are summed. A chunk found by both retrievers outranks one
///   found by only one.
/// </item>
/// <item>
///   If an <see cref="IReranker"/> is registered, the full fused list is passed to it
///   and the top <c>TopK</c> are returned. Without a reranker the fused list is
///   trimmed to <c>TopK</c> directly.
/// </item>
/// </list>
/// </remarks>
public sealed class HybridRetrievalPipeline : IRetrievalPipeline, IVectorQueryPipeline
{
    private readonly IRetriever _vectorRetriever;
    private readonly ILexicalRetriever _lexicalRetriever;
    private readonly IReranker? _reranker;
    private readonly HybridRetrievalOptions _options;
    private readonly ILogger<HybridRetrievalPipeline>? _logger;

    /// <summary>Initializes a new instance with all required and optional components.</summary>
    public HybridRetrievalPipeline(
        IRetriever vectorRetriever,
        ILexicalRetriever lexicalRetriever,
        IReranker? reranker = null,
        IOptions<HybridRetrievalOptions>? options = null,
        ILogger<HybridRetrievalPipeline>? logger = null)
    {
        _vectorRetriever = vectorRetriever;
        _lexicalRetriever = lexicalRetriever;
        _reranker = reranker;
        _options = options?.Value ?? new HybridRetrievalOptions();
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchResult>> QueryAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();
        var candidateOptions = CandidateOptions(opts);
        var vectorTask = _vectorRetriever.RetrieveAsync(query, candidateOptions, cancellationToken);
        return FuseAndRerankAsync(query, opts, candidateOptions, vectorTask, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchResult>> QueryByVectorAsync(
        float[] embedding,
        string query,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        var candidateOptions = CandidateOptions(options);
        // Reuse the precomputed embedding for the vector arm; the lexical arm and reranker still
        // use the query string. Fall back to the string overload if the retriever lacks the seam.
        var vectorTask = _vectorRetriever is IVectorRetriever vectorRetriever
            ? vectorRetriever.RetrieveByVectorAsync(embedding, candidateOptions, cancellationToken)
            : _vectorRetriever.RetrieveAsync(query, candidateOptions, cancellationToken);
        return FuseAndRerankAsync(query, options, candidateOptions, vectorTask, cancellationToken);
    }

    private RetrievalOptions CandidateOptions(RetrievalOptions opts) => new()
    {
        TopK = opts.TopK * _options.CandidateMultiplier,
        MinScore = opts.MinScore,
        MetadataFilter = opts.MetadataFilter
    };

    private async Task<IReadOnlyList<SearchResult>> FuseAndRerankAsync(
        string query,
        RetrievalOptions opts,
        RetrievalOptions candidateOptions,
        Task<IReadOnlyList<SearchResult>> vectorTask,
        CancellationToken cancellationToken)
    {
        var lexicalTask = _lexicalRetriever.RetrieveAsync(query, candidateOptions, cancellationToken);
        await Task.WhenAll(vectorTask, lexicalTask);

        var vectorResults = vectorTask.Result;
        var lexicalResults = lexicalTask.Result;

        var fused = Fuse(vectorResults, lexicalResults);
        _logger?.LogDebug(
            "Hybrid: {V} vector + {L} lexical → {F} fused.",
            vectorResults.Count, lexicalResults.Count, fused.Count);

        if (_reranker is null)
            return fused.Count <= opts.TopK ? fused : fused.Take(opts.TopK).ToList();

        var reranked = await _reranker.RerankAsync(query, fused, opts.TopK, cancellationToken);
        _logger?.LogDebug("Reranked to {Count} results.", reranked.Count);
        return reranked;
    }

    // Returns all fused candidates ordered by RRF score without trimming.
    // The reranker receives the full list; without a reranker the caller trims to TopK.
    private IReadOnlyList<SearchResult> Fuse(
        IReadOnlyList<SearchResult> vectorResults,
        IReadOnlyList<SearchResult> lexicalResults)
    {
        var scores = new Dictionary<string, float>();

        for (var i = 0; i < vectorResults.Count; i++)
        {
            var id = vectorResults[i].Chunk.Id!;
            scores[id] = scores.GetValueOrDefault(id) + 1f / (_options.RrfK + i + 1);
        }

        for (var i = 0; i < lexicalResults.Count; i++)
        {
            var id = lexicalResults[i].Chunk.Id!;
            scores[id] = scores.GetValueOrDefault(id) + 1f / (_options.RrfK + i + 1);
        }

        var chunkById = vectorResults.Concat(lexicalResults)
            .GroupBy(r => r.Chunk.Id!)
            .ToDictionary(g => g.Key, g => g.First().Chunk);

        return scores
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new SearchResult(chunkById[kv.Key], kv.Value))
            .ToList();
    }
}
