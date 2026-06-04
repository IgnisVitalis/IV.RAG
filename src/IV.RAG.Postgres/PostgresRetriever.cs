using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace IV.RAG;

/// <summary>Retrieves chunks from PostgreSQL using pgvector cosine similarity search.</summary>
public sealed class PostgresRetriever : IRetriever, IVectorRetriever
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbedder _embedder;
    private readonly string _tableName;

    /// <summary>Initializes a new instance with the provided data source, embedder, and options.</summary>
    public PostgresRetriever(NpgsqlDataSource dataSource, IEmbedder embedder, IOptions<PostgresOptions> options)
    {
        _dataSource = dataSource;
        _embedder = embedder;
        _tableName = SqlIdentifier.Validate(options.Value.TableName, nameof(PostgresOptions.TableName));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        var embedding = await _embedder.EmbedAsync(query, cancellationToken);
        return await RetrieveByVectorAsync(embedding, options, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> RetrieveByVectorAsync(
        float[] embedding,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand();

        var filterClause = string.Empty;
        if (options.MetadataFilter is not null)
        {
            var (filterSql, filterParams) = MetadataFilterSqlBuilder.Build(options.MetadataFilter);
            filterClause = $"\n          AND {filterSql}";
            foreach (var p in filterParams)
                cmd.Parameters.Add(p);
        }

        // <=> is cosine distance [0, 2]; converting to cosine similarity [-1, 1]
        // Uses > (not >=) so MinScore = 0.0 excludes orthogonal chunks (score exactly 0)
        cmd.CommandText = $"""
            SELECT id, text, metadata, 1 - (embedding <=> @embedding) AS score,
                   source_id, document_type, document_id, chunk_index
            FROM {_tableName}
            WHERE 1 - (embedding <=> @embedding) > @minScore{filterClause}
            ORDER BY embedding <=> @embedding
            LIMIT @topK
            """;

        cmd.Parameters.AddWithValue("embedding", new Vector(embedding));
        cmd.Parameters.AddWithValue("minScore", (double)options.MinScore);
        cmd.Parameters.AddWithValue("topK", options.TopK);

        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var metadata = reader.IsDBNull(2)
                ? null
                : JsonSerializer.Deserialize<Metadata>(reader.GetString(2));

            var origin = new Document.Origin(
                reader.GetGuid(4),
                reader.GetString(5),
                reader.GetString(6));

            var chunk = new Chunk
            {
                Id = reader.GetString(0),
                Text = reader.GetString(1),
                Metadata = metadata,
                Origin = origin,
                ChunkIndex = reader.IsDBNull(7) ? null : reader.GetInt32(7)
            };

            results.Add(new SearchResult(chunk, (float)reader.GetDouble(3)));
        }

        return results;
    }
}
