using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace IV.RAG;

/// <summary>Stores and manages chunks in a PostgreSQL table using pgvector.</summary>
public sealed class PostgresVectorStore : IVectorStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOptions _options;
    private int _schemaInitialized;

    /// <summary>Initializes a new instance with the provided data source and options.</summary>
    public PostgresVectorStore(NpgsqlDataSource dataSource, IOptions<PostgresOptions> options)
    {
        _dataSource = dataSource;
        _options = options.Value;
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
                INSERT INTO {_options.TableName} (id, text, embedding, metadata, source_id, document_type, document_id, chunk_index)
                VALUES (@id, @text, @embedding, @metadata::jsonb, @sourceId, @documentType, @documentId, @chunkIndex)
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

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _schemaInitialized, 1) != 0)
            return;

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_options.TableName} (
                id            TEXT PRIMARY KEY,
                text          TEXT NOT NULL,
                embedding     vector({_options.VectorDimension}) NOT NULL,
                metadata      JSONB,
                source_id     UUID NOT NULL,
                document_type TEXT NOT NULL,
                document_id   TEXT NOT NULL,
                chunk_index   INT
            );
            CREATE INDEX IF NOT EXISTS {_options.TableName}_origin_idx
                ON {_options.TableName} (source_id, document_type, document_id);
            """;

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
