namespace IV.RAG;

/// <summary>Options for <see cref="HybridRetrievalPipeline"/>.</summary>
public sealed class HybridRetrievalOptions
{
    /// <summary>
    /// The k constant in the RRF formula: score(d) = Σ 1 / (k + rank(d)).
    /// Higher values reduce the advantage of top-ranked results, distributing scores more evenly.
    /// The widely accepted default is 60.
    /// </summary>
    public int RrfK { get; set; } = 60;

    /// <summary>
    /// Number of candidates fetched from each sub-retriever, expressed as a multiple of
    /// <see cref="RetrievalOptions.TopK"/>. Defaults to 3 — e.g., TopK=5 fetches 15 candidates
    /// per source before fusion. Increase if final result quality is poor with the default.
    /// </summary>
    public int CandidateMultiplier { get; set; } = 3;
}
