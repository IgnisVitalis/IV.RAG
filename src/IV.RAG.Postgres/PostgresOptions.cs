namespace IV.RAG;

/// <summary>Configuration for the Postgres/pgvector provider.</summary>
public sealed class PostgresOptions
{
    /// <summary>Npgsql connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Table name used to store chunks. Defaults to <c>chunks</c>.</summary>
    public string TableName { get; set; } = "chunks";

    /// <summary>
    /// Dimensionality of the embedding vectors.
    /// Must match the model used by <see cref="IEmbedder"/>.
    /// Defaults to 768 (<c>nomic-embed-text</c>).
    /// </summary>
    public int VectorDimension { get; set; } = 768;

    /// <summary>
    /// PostgreSQL text search configuration used for full-text indexing and lexical retrieval.
    /// Must be a valid PostgreSQL text search configuration name (e.g., <c>"english"</c>,
    /// <c>"french"</c>, <c>"german"</c>). Use <c>"simple"</c> for language-agnostic matching
    /// without stemming or stop-word removal.
    /// Defaults to <c>"english"</c>.
    /// </summary>
    public string TextSearchLanguage { get; set; } = "english";

    /// <summary>
    /// Table name used to store query cache entries. Defaults to <c>query_cache</c>.
    /// </summary>
    public string QueryCacheTableName { get; set; } = "query_cache";
}
