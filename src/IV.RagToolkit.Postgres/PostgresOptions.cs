namespace IV.RagToolkit;

/// <summary>Configuration for the Postgres/pgvector provider.</summary>
public sealed class PostgresOptions
{
    /// <summary>Npgsql connection string.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Table name used to store chunks. Defaults to <c>chunks</c>.</summary>
    public string TableName { get; init; } = "chunks";

    /// <summary>
    /// Dimensionality of the embedding vectors.
    /// Must match the model used by <see cref="IEmbedder"/>.
    /// Defaults to 768 (<c>nomic-embed-text</c>).
    /// </summary>
    public int VectorDimension { get; init; } = 768;
}
