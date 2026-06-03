namespace IV.RAG;

/// <summary>Approximate nearest-neighbor (ANN) index strategy for the vector column.</summary>
public enum VectorIndexType
{
    /// <summary>
    /// No ANN index. Every similarity query is an exact sequential scan — accurate, but its
    /// latency grows linearly with the corpus size.
    /// </summary>
    None,

    /// <summary>
    /// HNSW (Hierarchical Navigable Small World) graph index. Builds incrementally as rows are
    /// inserted, so it performs well even when created on an initially empty table. Recommended default.
    /// </summary>
    Hnsw,

    /// <summary>
    /// IVFFlat (inverted file) index. Derives its cluster centroids from existing rows, so it should
    /// be rebuilt (<c>REINDEX</c>) <em>after</em> the table holds a representative amount of data — an
    /// index created on an empty table gives poor recall until it is rebuilt.
    /// </summary>
    IVFFlat
}

/// <summary>Configuration for the Postgres/pgvector provider.</summary>
public sealed class PostgresOptions
{
    /// <summary>Npgsql connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Table name used to store chunks. Defaults to <c>chunks</c>.</summary>
    public string TableName { get; set; } = "chunks";

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

    /// <summary>
    /// ANN index strategy for the <c>embedding</c> column. Defaults to <see cref="VectorIndexType.Hnsw"/>.
    /// Without an index, similarity search is an exact sequential scan whose latency grows linearly
    /// with the corpus size.
    /// </summary>
    /// <remarks>
    /// The index always uses the <c>vector_cosine_ops</c> opclass to match the cosine distance
    /// operator (<c>&lt;=&gt;</c>) used by <see cref="PostgresRetriever"/>; an opclass that does not
    /// match that operator would never be used by the planner. pgvector can only index vectors of up
    /// to 2000 dimensions on the <c>vector</c> type — for higher-dimension models the index is skipped
    /// (with a warning) and queries fall back to an exact scan.
    /// </remarks>
    public VectorIndexType VectorIndex { get; set; } = VectorIndexType.Hnsw;

    /// <summary>
    /// HNSW <c>m</c> parameter — the maximum number of connections per graph layer. Higher values
    /// improve recall at the cost of index size and build time. Used only when <see cref="VectorIndex"/>
    /// is <see cref="VectorIndexType.Hnsw"/>. Must be at least 2. Defaults to <c>16</c> (the pgvector default).
    /// </summary>
    public int HnswM { get; set; } = 16;

    /// <summary>
    /// HNSW <c>ef_construction</c> parameter — the size of the candidate list maintained during the
    /// index build. Higher values improve recall at the cost of build time. Must be at least
    /// <c>2 × </c><see cref="HnswM"/>. Used only when <see cref="VectorIndex"/> is
    /// <see cref="VectorIndexType.Hnsw"/>. Defaults to <c>64</c> (the pgvector default).
    /// </summary>
    public int HnswEfConstruction { get; set; } = 64;

    /// <summary>
    /// IVFFlat <c>lists</c> parameter — the number of inverted lists (clusters). pgvector suggests
    /// <c>rows / 1000</c> for tables up to 1M rows, and <c>sqrt(rows)</c> beyond that. Must be at
    /// least 1. Used only when <see cref="VectorIndex"/> is <see cref="VectorIndexType.IVFFlat"/>.
    /// Defaults to <c>100</c>.
    /// </summary>
    public int IVFFlatLists { get; set; } = 100;
}
