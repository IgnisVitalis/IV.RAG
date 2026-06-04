namespace IV.RAG;

/// <summary>Controls how results are filtered and ranked during retrieval.</summary>
public sealed record RetrievalOptions
{
    /// <summary>Maximum number of chunks to return.</summary>
    public int TopK { get; init; } = 5;

    /// <summary>
    /// Minimum cosine similarity score in [-1, 1] a chunk must have to be included.
    /// Defaults to 0.0 — excludes unrelated and opposite-meaning results.
    /// </summary>
    public float MinScore { get; init; } = 0.0f;

    /// <summary>
    /// Optional predicate applied to chunk metadata before returning results.
    /// Only chunks whose metadata satisfies the filter are included.
    /// </summary>
    public MetadataFilter? MetadataFilter { get; init; }

    /// <summary>
    /// Optional source-system scope. When set, only chunks whose origin <c>SourceId</c> matches are
    /// returned. Combined with <see cref="DocumentType"/> and <see cref="DocumentId"/>, this is the
    /// toolkit's access-control primitive: the application resolves the scope the current user is
    /// permitted and the toolkit enforces it in the retrieval query.
    /// </summary>
    public Guid? SourceId { get; init; }

    /// <summary>Optional document-type scope. When set, only chunks of this document type are returned.</summary>
    public string? DocumentType { get; init; }

    /// <summary>Optional document scope. When set, only chunks from this document id are returned.</summary>
    public string? DocumentId { get; init; }
}
