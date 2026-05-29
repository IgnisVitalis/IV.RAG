using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace IV.RagToolkit;

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
    public async Task UpsertAsync(IEnumerable<Chunk> chunks, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        foreach (var chunk in chunks)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {_options.TableName} (id, text, embedding, metadata)
                VALUES (@id, @text, @embedding, @metadata::jsonb)
                ON CONFLICT (id) DO UPDATE SET
                    text = EXCLUDED.text,
                    embedding = EXCLUDED.embedding,
                    metadata = EXCLUDED.metadata
                """;

            cmd.Parameters.AddWithValue("id", chunk.Id!);
            cmd.Parameters.AddWithValue("text", chunk.Text);
            cmd.Parameters.AddWithValue("embedding", new Vector(chunk.Embedding!));
            cmd.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb,
                chunk.Metadata is not null ? (object)JsonSerializer.Serialize(chunk.Metadata) : DBNull.Value);

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

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _schemaInitialized, 1) != 0)
            return;

        await using var cmd = _dataSource.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_options.TableName} (
                id        TEXT PRIMARY KEY,
                text      TEXT NOT NULL,
                embedding vector({_options.VectorDimension}) NOT NULL,
                metadata  JSONB
            );
            """;

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
