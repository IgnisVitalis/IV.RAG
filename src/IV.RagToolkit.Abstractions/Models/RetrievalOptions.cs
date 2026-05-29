namespace IV.RagToolkit;

/// <summary>Controls how results are filtered and ranked during retrieval.</summary>
public sealed class RetrievalOptions
{
    /// <summary>Maximum number of chunks to return.</summary>
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Minimum cosine similarity score in [-1, 1] a chunk must have to be included.
    /// Defaults to 0.0 — excludes unrelated and opposite-meaning results.
    /// </summary>
    public float MinScore { get; init; } = 0.0f;
}
