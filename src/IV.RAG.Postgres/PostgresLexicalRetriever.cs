using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IV.RAG;

/// <summary>
/// Retrieves chunks from PostgreSQL using full-text search (<c>tsvector</c> / <c>tsquery</c>).
/// Scores are computed by <c>ts_rank</c>. Results are ordered by descending rank
/// and capped at <see cref="RetrievalOptions.TopK"/>.
/// </summary>
/// <remarks>
/// Requires the <c>text_search</c> GIN-indexed column created by
/// <see cref="PostgresVectorStore.SetAsync"/> (via <c>EnsureSchemaAsync</c>).
/// The text search configuration (<see cref="PostgresOptions.TextSearchLanguage"/>)
/// must match the language used during ingestion.
/// </remarks>
public sealed class PostgresLexicalRetriever : ILexicalRetriever
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOptions _options;

    /// <summary>Initializes a new instance with the provided data source and options.</summary>
    public PostgresLexicalRetriever(NpgsqlDataSource dataSource, IOptions<PostgresOptions> options)
    {
        _dataSource = dataSource;
        _options = options.Value;
        SqlIdentifier.Validate(_options.TableName, nameof(PostgresOptions.TableName));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> RetrieveAsync(
        string query,
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

        cmd.CommandText = $"""
            SELECT id, text, metadata, ts_rank(text_search, plainto_tsquery(@lang::regconfig, @query)) AS score,
                   source_id, document_type, document_id, chunk_index
            FROM {_options.TableName}
            WHERE text_search @@ plainto_tsquery(@lang::regconfig, @query){filterClause}
            ORDER BY score DESC
            LIMIT @topK
            """;

        cmd.Parameters.AddWithValue("lang", _options.TextSearchLanguage);
        cmd.Parameters.AddWithValue("query", query);
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
