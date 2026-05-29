using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace IV.RagToolkit;

/// <summary>Retrieves chunks from PostgreSQL using pgvector cosine similarity search.</summary>
public sealed class PostgresRetriever : IRetriever
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _tableName;

    /// <summary>Initializes a new instance with the provided data source and options.</summary>
    public PostgresRetriever(NpgsqlDataSource dataSource, IOptions<PostgresOptions> options)
    {
        _dataSource = dataSource;
        _tableName = options.Value.TableName;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        float[] embedding,
        RetrievalOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = _dataSource.CreateCommand();

        // <=> is cosine distance [0, 2]; converting to cosine similarity [-1, 1]
        // Uses > (not >=) so MinScore = 0.0 excludes orthogonal chunks (score exactly 0)
        cmd.CommandText = $"""
            SELECT id, text, metadata, 1 - (embedding <=> @embedding) AS score
            FROM {_tableName}
            WHERE 1 - (embedding <=> @embedding) > @minScore
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
                : JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(2)) as IReadOnlyDictionary<string, object>;

            var chunk = new Chunk
            {
                Id = reader.GetString(0),
                Text = reader.GetString(1),
                Metadata = metadata
            };

            results.Add(new SearchResult(chunk, (float)reader.GetDouble(3)));
        }

        return results;
    }
}
