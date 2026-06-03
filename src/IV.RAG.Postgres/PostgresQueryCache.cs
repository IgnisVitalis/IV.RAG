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
    private readonly string _tableName;
    private readonly int _vectorDimension;
    private readonly QueryCacheOptions _cacheOptions;
    private int _schemaInitialized;

    /// <summary>Initializes a new instance with the provided data source and options.</summary>
    public PostgresQueryCache(
        NpgsqlDataSource dataSource,
        IOptions<PostgresOptions> postgresOptions,
        IOptions<QueryCacheOptions> cacheOptions)
    {
        _dataSource = dataSource;
        _tableName = postgresOptions.Value.QueryCacheTableName;
        _vectorDimension = postgresOptions.Value.VectorDimension;
        _cacheOptions = cacheOptions.Value;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>?> GetAsync(
        float[] queryEmbedding,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        var optionsHash = JsonSerializer.Serialize(options, JsonOptions);
        // cosine distance = 1 - cosine similarity; threshold on similarity → ceiling on distance
        var distanceThreshold = 1f - _cacheOptions.SimilarityThreshold;

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            SELECT results
            FROM {_tableName}
            WHERE expires_at > NOW()
              AND options_hash = @optionsHash
              AND (query_embedding <=> @embedding) <= @distanceThreshold
            ORDER BY query_embedding <=> @embedding
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("optionsHash", optionsHash);
        cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue("distanceThreshold", (double)distanceThreshold);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var json = reader.GetString(0);
        return JsonSerializer.Deserialize<List<SearchResult>>(json, JsonOptions);
    }

    /// <inheritdoc/>
    public async Task SetAsync(
        float[] queryEmbedding,
        RetrievalOptions options,
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        var optionsHash = JsonSerializer.Serialize(options, JsonOptions);
        var resultsJson = JsonSerializer.Serialize(results, JsonOptions);
        var origins = results
            .Select(r => FormatOrigin(r.Chunk.Origin))
            .Distinct()
            .ToArray();
        var expiresAt = DateTimeOffset.UtcNow.Add(_cacheOptions.Ttl);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await using (var cleanCmd = conn.CreateCommand())
        {
            cleanCmd.Transaction = tx;
            cleanCmd.CommandText = $"DELETE FROM {_tableName} WHERE expires_at <= NOW()";
            await cleanCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = $"""
                INSERT INTO {_tableName} (query_embedding, options_hash, results, document_origins, expires_at)
                VALUES (@embedding, @optionsHash, @results, @origins, @expiresAt)
                """;
            insertCmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
            insertCmd.Parameters.AddWithValue("optionsHash", optionsHash);
            insertCmd.Parameters.AddWithValue("results", NpgsqlDbType.Jsonb, resultsJson);
            insertCmd.Parameters.Add(new NpgsqlParameter("origins", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = origins });
            insertCmd.Parameters.AddWithValue("expiresAt", expiresAt);
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

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_tableName} (
                id               BIGSERIAL PRIMARY KEY,
                query_embedding  vector({_vectorDimension}) NOT NULL,
                options_hash     TEXT NOT NULL,
                results          JSONB NOT NULL,
                document_origins TEXT[] NOT NULL,
                expires_at       TIMESTAMPTZ NOT NULL
            );
            CREATE INDEX IF NOT EXISTS {_tableName}_expires_idx ON {_tableName} (expires_at);
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        Volatile.Write(ref _schemaInitialized, 1);
    }

    private static string FormatOrigin(Document.Origin o) =>
        $"{o.SourceId:N}|{o.DocumentType}|{o.DocumentId}";
}
