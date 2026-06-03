using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace IV.RAG;

/// <summary>PostgreSQL-backed implementation of <see cref="IQueryCache"/> using pgvector cosine similarity.</summary>
public sealed class PostgresQueryCache : IQueryCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbedder _embedder;
    private readonly string _tableName;
    private readonly QueryCacheOptions _cacheOptions;
    private int _schemaInitialized;

    /// <summary>Initializes a new instance with the provided data source, embedder, and options.</summary>
    public PostgresQueryCache(
        NpgsqlDataSource dataSource,
        IEmbedder embedder,
        IOptions<PostgresOptions> postgresOptions,
        IOptions<QueryCacheOptions> cacheOptions)
    {
        _dataSource = dataSource;
        _embedder = embedder;
        _tableName = postgresOptions.Value.QueryCacheTableName;
        _cacheOptions = cacheOptions.Value;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>?> GetAsync(
        float[] queryEmbedding,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        var model = _embedder.ModelInfo;
        var optionsHash = JsonSerializer.Serialize(options, JsonOptions);
        var distanceThreshold = 1f - _cacheOptions.SimilarityThreshold;

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            SELECT results
            FROM {_tableName}
            WHERE expires_at > NOW()
              AND options_hash = @optionsHash
              AND embedder_provider = @provider
              AND embedder_model = @model
              AND embedder_dimensions = @dims
              AND (query_embedding <=> @embedding) <= @distanceThreshold
            ORDER BY query_embedding <=> @embedding
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("optionsHash", optionsHash);
        cmd.Parameters.AddWithValue("provider", model.Provider);
        cmd.Parameters.AddWithValue("model", model.ModelName);
        cmd.Parameters.AddWithValue("dims", NpgsqlDbType.Integer, model.Dimensions);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("distanceThreshold", (double)distanceThreshold);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return JsonSerializer.Deserialize<List<SearchResult>>(reader.GetString(0), JsonOptions);
    }

    /// <inheritdoc/>
    public async Task SetAsync(
        float[] queryEmbedding,
        RetrievalOptions options,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        var model = _embedder.ModelInfo;
        var optionsHash = JsonSerializer.Serialize(options, JsonOptions);
        var resultsJson = JsonSerializer.Serialize(results, JsonOptions);
        var origins = results.Select(r => FormatOrigin(r.Chunk.Origin)).Distinct().ToArray();
        var expiresAt = DateTimeOffset.UtcNow.Add(_cacheOptions.Ttl);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await using (var cleanCmd = conn.CreateCommand())
        {
            cleanCmd.Transaction = tx;
            // Remove expired entries AND entries from any other embedding model
            cleanCmd.CommandText = $"""
                DELETE FROM {_tableName}
                WHERE expires_at <= NOW()
                   OR embedder_provider IS DISTINCT FROM @provider
                   OR embedder_model    IS DISTINCT FROM @model
                   OR embedder_dimensions IS DISTINCT FROM @dims
                """;
            cleanCmd.Parameters.AddWithValue("provider", model.Provider);
            cleanCmd.Parameters.AddWithValue("model", model.ModelName);
            cleanCmd.Parameters.AddWithValue("dims", NpgsqlDbType.Integer, model.Dimensions);
            await cleanCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = $"""
                INSERT INTO {_tableName}
                    (query_embedding, options_hash, results, document_origins, expires_at,
                     embedder_provider, embedder_model, embedder_dimensions)
                VALUES
                    (@embedding, @optionsHash, @results, @origins, @expiresAt,
                     @provider, @model, @dims)
                """;
            insertCmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
            insertCmd.Parameters.AddWithValue("optionsHash", optionsHash);
            insertCmd.Parameters.AddWithValue("results", NpgsqlDbType.Jsonb, resultsJson);
            insertCmd.Parameters.Add(new NpgsqlParameter("origins", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = origins });
            insertCmd.Parameters.AddWithValue("expiresAt", expiresAt);
            insertCmd.Parameters.AddWithValue("provider", model.Provider);
            insertCmd.Parameters.AddWithValue("model", model.ModelName);
            insertCmd.Parameters.AddWithValue("dims", NpgsqlDbType.Integer, model.Dimensions);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task InvalidateByDocumentAsync(
        Document.Origin origin,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_tableName} WHERE @originKey = ANY(document_origins)";
        cmd.Parameters.AddWithValue("originKey", FormatOrigin(origin));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaInitialized) != 0)
            return;

        var dims = _embedder.ModelInfo.Dimensions;

        await using (var cmd = _dataSource.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {_tableName} (
                    id                  BIGSERIAL PRIMARY KEY,
                    query_embedding     vector({dims}) NOT NULL,
                    options_hash        TEXT NOT NULL,
                    results             JSONB NOT NULL,
                    document_origins    TEXT[] NOT NULL,
                    expires_at          TIMESTAMPTZ NOT NULL,
                    embedder_provider   TEXT,
                    embedder_model      TEXT,
                    embedder_dimensions INT
                );
                ALTER TABLE {_tableName} ADD COLUMN IF NOT EXISTS embedder_provider   TEXT;
                ALTER TABLE {_tableName} ADD COLUMN IF NOT EXISTS embedder_model      TEXT;
                ALTER TABLE {_tableName} ADD COLUMN IF NOT EXISTS embedder_dimensions INT;
                CREATE INDEX IF NOT EXISTS {_tableName}_expires_idx ON {_tableName} (expires_at);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await AdaptColumnDimensionIfNeededAsync(dims, cancellationToken);

        Volatile.Write(ref _schemaInitialized, 1);
    }

    private async Task AdaptColumnDimensionIfNeededAsync(int requiredDimensions, CancellationToken ct)
    {
        await using var checkCmd = _dataSource.CreateCommand();
        checkCmd.CommandText = """
            SELECT pa.atttypmod FROM pg_attribute pa
            WHERE pa.attrelid = to_regclass(@tableName)
              AND pa.attname = 'query_embedding'
              AND pa.attnum > 0 AND NOT pa.attisdropped
            """;
        checkCmd.Parameters.AddWithValue("tableName", _tableName);
        var result = await checkCmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return;

        var storedDimensions = (int)result;
        if (storedDimensions == requiredDimensions) return;

        // All cached entries are invalid after a dimension change — truncate and retype
        await using var alterCmd = _dataSource.CreateCommand();
        alterCmd.CommandText = $"""
            TRUNCATE TABLE {_tableName};
            ALTER TABLE {_tableName} ALTER COLUMN query_embedding TYPE vector({requiredDimensions});
            """;
        await alterCmd.ExecuteNonQueryAsync(ct);
    }

    private static string FormatOrigin(Document.Origin o) =>
        $"{o.SourceId:N}|{o.DocumentType}|{o.DocumentId}";
}
