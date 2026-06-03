using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace IV.RAG;

/// <summary>Stores and manages chunks in a PostgreSQL table using pgvector.</summary>
public sealed class PostgresVectorStore : IVectorStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbedder _embedder;
    private readonly PostgresOptions _options;
    private readonly ILogger<PostgresVectorStore>? _logger;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaInitialized;
    private int _currentModelId;

    // pgvector can only build an HNSW/IVFFlat index on vectors of up to 2000 dimensions
    // (on the standard 'vector' type). Above this, index creation is skipped with a warning.
    private const int MaxIndexableDimensions = 2000;

    /// <summary>Initializes a new instance with the provided data source, embedder, and options.</summary>
    public PostgresVectorStore(NpgsqlDataSource dataSource, IEmbedder embedder, IOptions<PostgresOptions> options, ILogger<PostgresVectorStore>? logger = null)
    {
        _dataSource = dataSource;
        _embedder = embedder;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task SetAsync(Document.Origin origin, IEnumerable<Chunk> chunks, CancellationToken cancellationToken = default)
    {
        var chunkList = chunks.ToList();

        var mismatch = chunkList.Find(c => c.Origin != origin);
        if (mismatch is not null)
            throw new ArgumentException(
                $"Chunk origin '{mismatch.Origin}' does not match the target origin '{origin}'.",
                nameof(chunks));

        var missingId = chunkList.Find(c => string.IsNullOrEmpty(c.Id));
        if (missingId is not null)
            throw new ArgumentException("All chunks must have a non-null, non-empty Id.", nameof(chunks));

        var missingEmbedding = chunkList.Find(c => c.Embedding is null);
        if (missingEmbedding is not null)
            throw new ArgumentException("All chunks must have a non-null Embedding.", nameof(chunks));

        await EnsureSchemaAsync(cancellationToken);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await using (var deleteCmd = conn.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = $"""
                DELETE FROM {_options.TableName}
                WHERE source_id = @sourceId
                  AND document_type = @documentType
                  AND document_id = @documentId
                """;
            deleteCmd.Parameters.Add(new NpgsqlParameter("sourceId", NpgsqlDbType.Uuid) { Value = origin.SourceId });
            deleteCmd.Parameters.AddWithValue("documentType", origin.DocumentType);
            deleteCmd.Parameters.AddWithValue("documentId", origin.DocumentId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var chunk in chunkList)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_options.TableName} (id, text, embedding, metadata, source_id, document_type, document_id, chunk_index, model_id)
                VALUES (@id, @text, @embedding, @metadata::jsonb, @sourceId, @documentType, @documentId, @chunkIndex, @modelId)
                """;
            cmd.Parameters.AddWithValue("id", chunk.Id!);
            cmd.Parameters.AddWithValue("text", chunk.Text);
            cmd.Parameters.AddWithValue("embedding", new Vector(chunk.Embedding!));
            cmd.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb,
                chunk.Metadata is not null ? (object)JsonSerializer.Serialize(chunk.Metadata) : DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("sourceId", NpgsqlDbType.Uuid) { Value = chunk.Origin.SourceId });
            cmd.Parameters.AddWithValue("documentType", chunk.Origin.DocumentType);
            cmd.Parameters.AddWithValue("documentId", chunk.Origin.DocumentId);
            cmd.Parameters.AddWithValue("chunkIndex", NpgsqlDbType.Integer,
                chunk.ChunkIndex.HasValue ? (object)chunk.ChunkIndex.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("modelId", NpgsqlDbType.Integer, _currentModelId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_options.TableName} WHERE id = ANY(@ids)";
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteByDocumentAsync(Document.Origin origin, CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {_options.TableName}
            WHERE source_id = @sourceId
              AND document_type = @documentType
              AND document_id = @documentId
            """;
        cmd.Parameters.Add(new NpgsqlParameter("sourceId", NpgsqlDbType.Uuid) { Value = origin.SourceId });
        cmd.Parameters.AddWithValue("documentType", origin.DocumentType);
        cmd.Parameters.AddWithValue("documentId", origin.DocumentId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> CountOutdatedAsync(CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken)) return 0;
        await EnsureSchemaAsync(cancellationToken);

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM {_options.TableName}
            WHERE model_id IS DISTINCT FROM @currentModelId OR embedding IS NULL
            """;
        cmd.Parameters.AddWithValue("currentModelId", NpgsqlDbType.Integer, _currentModelId);
        return (int)(long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Chunk> GetOutdatedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await TableExistsAsync(cancellationToken)) yield break;
        await EnsureSchemaAsync(cancellationToken);

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, text, metadata, source_id, document_type, document_id, chunk_index
            FROM {_options.TableName}
            WHERE model_id IS DISTINCT FROM @currentModelId OR embedding IS NULL
            """;
        cmd.Parameters.AddWithValue("currentModelId", NpgsqlDbType.Integer, _currentModelId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var metadata = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<Metadata>(reader.GetString(2));
            var origin = new Document.Origin(reader.GetGuid(3), reader.GetString(4), reader.GetString(5));
            yield return new Chunk
            {
                Id = reader.GetString(0),
                Text = reader.GetString(1),
                Metadata = metadata,
                Origin = origin,
                ChunkIndex = reader.IsDBNull(6) ? null : reader.GetInt32(6)
            };
        }
    }

    /// <inheritdoc/>
    public async Task UpdateEmbeddingAsync(
        string id,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {_options.TableName}
            SET embedding = @embedding, model_id = @modelId
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("embedding", new Vector(embedding));
        cmd.Parameters.AddWithValue("modelId", NpgsqlDbType.Integer, _currentModelId);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaInitialized) return; // fast path — no lock needed after init

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaInitialized) return; // double-check inside lock

            ValidateLanguage(_options.TextSearchLanguage);
            ValidateVectorIndexOptions();

            var table = _options.TableName;
            var modelsTable = $"{table}_models";
            var dimensions = await ResolveDimensionsAsync(cancellationToken);
            var language = _options.TextSearchLanguage;

            // Phase 1: create tables and add missing columns
            await using (var cmd = _dataSource.CreateCommand())
            {
                cmd.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS {modelsTable} (
                        id         SERIAL PRIMARY KEY,
                        provider   TEXT NOT NULL,
                        model_name TEXT NOT NULL,
                        dimensions INT  NOT NULL,
                        UNIQUE (provider, model_name, dimensions)
                    );
                    CREATE TABLE IF NOT EXISTS {table} (
                        id            TEXT PRIMARY KEY,
                        text          TEXT NOT NULL,
                        embedding     vector({dimensions}) NOT NULL,
                        metadata      JSONB,
                        source_id     UUID NOT NULL,
                        document_type TEXT NOT NULL,
                        document_id   TEXT NOT NULL,
                        chunk_index   INT,
                        model_id      INT REFERENCES {modelsTable}(id),
                        text_search   TSVECTOR GENERATED ALWAYS AS (to_tsvector('{language}'::regconfig, text)) STORED
                    );
                    ALTER TABLE {table} ADD COLUMN IF NOT EXISTS model_id INT REFERENCES {modelsTable}(id);
                    CREATE INDEX IF NOT EXISTS {table}_origin_idx
                        ON {table} (source_id, document_type, document_id);
                    CREATE INDEX IF NOT EXISTS {table}_model_id_idx
                        ON {table} (model_id);
                    CREATE INDEX IF NOT EXISTS {table}_text_search_idx
                        ON {table} USING GIN (text_search);
                    """;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Phase 2: adapt vector column dimension if it changed
            await AdaptColumnDimensionIfNeededAsync(dimensions, cancellationToken);

            // Phase 2b: create the ANN index on the embedding column. Runs after the column
            // dimension is settled so a dimension change (which drops the index in Phase 2)
            // recreates it at the correct size.
            await EnsureVectorIndexAsync(dimensions, cancellationToken);

            // Phase 3: upsert current model to get its id
            _currentModelId = await UpsertModelAsync(dimensions, cancellationToken);

            // Mark initialized here so _currentModelId is usable even when Phase 4 throws.
            // Concurrent callers blocked on the semaphore will see _schemaInitialized = true
            // and return early with a valid _currentModelId.
            _schemaInitialized = true;

            // Phase 4: throw if any chunks were embedded with a different model
            await ThrowIfMismatchAsync(cancellationToken);

            // Phase 5: tighten model_id NOT NULL now that all rows are tracked
            await TryTightenModelConstraintAsync(cancellationToken);
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task AdaptColumnDimensionIfNeededAsync(int requiredDimensions, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand();
        // to_regclass returns NULL instead of throwing when the table does not exist yet
        cmd.CommandText = """
            SELECT pa.atttypmod
            FROM pg_attribute pa
            WHERE pa.attrelid = to_regclass(@tableName)
              AND pa.attname = 'embedding'
              AND pa.attnum > 0
              AND NOT pa.attisdropped
            """;
        cmd.Parameters.AddWithValue("tableName", _options.TableName);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is null or DBNull) return;

        var storedDimensions = (int)result;
        if (storedDimensions == requiredDimensions) return;

        // Dimension changed: wipe existing vectors so the column type can be altered.
        // Text is preserved; IEmbeddingMigrator.MigrateAsync() re-embeds everything.
        // The vector index is dropped first because it depends on the column type;
        // EnsureVectorIndexAsync recreates it at the new dimension.
        await using var alterCmd = _dataSource.CreateCommand();
        alterCmd.CommandText = $"""
            DROP INDEX IF EXISTS {_options.TableName}_embedding_idx;
            ALTER TABLE {_options.TableName} ALTER COLUMN embedding DROP NOT NULL;
            ALTER TABLE {_options.TableName} ALTER COLUMN embedding TYPE vector({requiredDimensions}) USING NULL;
            """;
        await alterCmd.ExecuteNonQueryAsync(ct);
        _logger?.LogWarning(
            "Embedding column dimension for table '{Table}' changed from {OldDims}d to {NewDims}d. " +
            "Existing vectors have been cleared. Run IEmbeddingMigrator.MigrateAsync() to re-embed all chunks.",
            _options.TableName, storedDimensions, requiredDimensions);
    }

    private async Task EnsureVectorIndexAsync(int dimensions, CancellationToken ct)
    {
        if (_options.VectorIndex == VectorIndexType.None) return;

        var indexName = $"{_options.TableName}_embedding_idx";

        if (dimensions > MaxIndexableDimensions)
        {
            _logger?.LogWarning(
                "Embedding dimension {Dimensions} exceeds the pgvector index limit of {Limit} for the " +
                "'vector' type; skipping creation of '{Index}'. Similarity queries on table '{Table}' will " +
                "use an exact sequential scan. Set PostgresOptions.VectorIndex = None to silence this, or " +
                "use a model with {Limit} or fewer dimensions.",
                dimensions, MaxIndexableDimensions, indexName, _options.TableName, MaxIndexableDimensions);
            return;
        }

        // m / ef_construction / lists are validated integers, so interpolating them into DDL is
        // injection-safe; pgvector index build parameters cannot be passed as query parameters.
        var indexClause = _options.VectorIndex switch
        {
            VectorIndexType.Hnsw =>
                $"USING hnsw (embedding vector_cosine_ops) WITH (m = {_options.HnswM}, ef_construction = {_options.HnswEfConstruction})",
            VectorIndexType.IVFFlat =>
                $"USING ivfflat (embedding vector_cosine_ops) WITH (lists = {_options.IVFFlatLists})",
            _ => throw new InvalidOperationException($"Unsupported vector index type '{_options.VectorIndex}'.")
        };

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {_options.TableName} {indexClause}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> UpsertModelAsync(int dimensions, CancellationToken ct)
    {
        var model = _embedder.ModelInfo;
        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_options.TableName}_models (provider, model_name, dimensions)
            VALUES (@provider, @modelName, @dimensions)
            ON CONFLICT (provider, model_name, dimensions) DO UPDATE SET provider = EXCLUDED.provider
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("provider", model.Provider);
        cmd.Parameters.AddWithValue("modelName", model.ModelName);
        cmd.Parameters.AddWithValue("dimensions", NpgsqlDbType.Integer, dimensions);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<bool> TableExistsAsync(CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";
        cmd.Parameters.AddWithValue("tableName", _options.TableName);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<int> ResolveDimensionsAsync(CancellationToken ct)
    {
        var dims = _embedder.ModelInfo.Dimensions;
        if (dims > 0) return dims;

        // EmbeddingDimensions not configured and no embed call made yet — read from existing column
        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = """
            SELECT pa.atttypmod FROM pg_attribute pa
            WHERE pa.attrelid = to_regclass(@tableName)
              AND pa.attname = 'embedding'
              AND pa.attnum > 0 AND NOT pa.attisdropped
            """;
        cmd.Parameters.AddWithValue("tableName", _options.TableName);
        var result = await cmd.ExecuteScalarAsync(ct);

        if (result is int d and > 0) return d;

        throw new InvalidOperationException(
            $"Cannot initialize vector table '{_options.TableName}': the embedding dimension is unknown. " +
            $"Set OllamaOptions.EmbeddingDimensions explicitly, or ensure at least one " +
            $"EmbedAsync call completes before the first store operation.");
    }

    private async Task ThrowIfMismatchAsync(CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.provider, m.model_name, m.dimensions
            FROM {_options.TableName} c
            LEFT JOIN {_options.TableName}_models m ON c.model_id = m.id
            WHERE c.model_id IS DISTINCT FROM @currentModelId OR c.embedding IS NULL
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("currentModelId", NpgsqlDbType.Integer, _currentModelId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return;

        EmbedderInfo? storedModel = reader.IsDBNull(0)
            ? null
            : new EmbedderInfo(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));

        _logger?.LogWarning(
            "Embedding model mismatch on table '{Table}': stored={Stored}, current={Current}. " +
            "Run IEmbeddingMigrator.MigrateAsync() to re-embed outdated chunks.",
            _options.TableName, storedModel?.ToString() ?? "unknown", _embedder.ModelInfo);
        throw new EmbeddingModelMismatchException(storedModel, _embedder.ModelInfo, _options.TableName);
    }

    private async Task TryTightenModelConstraintAsync(CancellationToken ct)
    {
        // Check whether model_id is already NOT NULL — skip the ALTER if so.
        await using var checkCmd = _dataSource.CreateCommand();
        checkCmd.CommandText = """
            SELECT pa.attnotnull
            FROM pg_attribute pa
            WHERE pa.attrelid = to_regclass(@tableName)
              AND pa.attname = 'model_id'
              AND pa.attnum > 0
              AND NOT pa.attisdropped
            """;
        checkCmd.Parameters.AddWithValue("tableName", _options.TableName);
        var result = await checkCmd.ExecuteScalarAsync(ct);
        if (result is true) return;

        await using var alterCmd = _dataSource.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {_options.TableName} ALTER COLUMN model_id SET NOT NULL";
        await alterCmd.ExecuteNonQueryAsync(ct);
        _logger?.LogInformation(
            "All chunks in table '{Table}' are tracked — added NOT NULL constraint to model_id.",
            _options.TableName);
    }

    private static readonly Regex SafeLanguage = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    private static void ValidateLanguage(string language)
    {
        if (!SafeLanguage.IsMatch(language))
            throw new ArgumentException(
                $"TextSearchLanguage '{language}' is invalid. Use only lowercase letters, digits, and underscores, starting with a letter.",
                nameof(language));
    }

    private void ValidateVectorIndexOptions()
    {
        switch (_options.VectorIndex)
        {
            case VectorIndexType.Hnsw:
                if (_options.HnswM < 2)
                    throw new ArgumentException(
                        $"HnswM must be at least 2 (was {_options.HnswM}).", nameof(PostgresOptions.HnswM));
                if (_options.HnswEfConstruction < 2 * _options.HnswM)
                    throw new ArgumentException(
                        $"HnswEfConstruction must be at least 2 × HnswM ({2 * _options.HnswM}); was {_options.HnswEfConstruction}.",
                        nameof(PostgresOptions.HnswEfConstruction));
                break;
            case VectorIndexType.IVFFlat:
                if (_options.IVFFlatLists < 1)
                    throw new ArgumentException(
                        $"IVFFlatLists must be at least 1 (was {_options.IVFFlatLists}).", nameof(PostgresOptions.IVFFlatLists));
                break;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _schemaLock.Dispose();
}
