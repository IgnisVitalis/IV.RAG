namespace IV.RAG;

/// <summary>
/// Maps between the remote query/response DTOs and the domain types, so the HTTP client and a
/// server implementation share a single wire contract instead of re-implementing the shape by hand.
/// </summary>
public static class RemoteContract
{
    /// <summary>Builds a <see cref="QueryRequest"/> from a query string and retrieval options (client side).</summary>
    public static QueryRequest ToQueryRequest(string query, RetrievalOptions options) =>
        new(query, options.TopK, options.MinScore, options.MetadataFilter);

    /// <summary>Reconstructs <see cref="RetrievalOptions"/> from a <see cref="QueryRequest"/> (server side).</summary>
    public static RetrievalOptions ToRetrievalOptions(this QueryRequest request) =>
        new() { TopK = request.TopK, MinScore = request.MinScore, MetadataFilter = request.MetadataFilter };

    /// <summary>Maps retrieval results to a <see cref="QueryResponse"/> (server side).</summary>
    public static QueryResponse ToQueryResponse(this IReadOnlyList<SearchResult> results) =>
        new(results.Select(ToDto).ToArray());

    /// <summary>Maps a <see cref="QueryResponse"/> back to domain search results (client side).</summary>
    public static IReadOnlyList<SearchResult> ToSearchResults(this QueryResponse response) =>
        response.Results.Select(ToSearchResult).ToList();

    /// <summary>Maps a <see cref="SearchResult"/> to its wire DTO.</summary>
    public static SearchResultDto ToDto(this SearchResult result) =>
        new(result.Chunk.ToDto(), result.Score);

    /// <summary>Maps a <see cref="SearchResultDto"/> back to a domain <see cref="SearchResult"/>.</summary>
    public static SearchResult ToSearchResult(this SearchResultDto dto) =>
        new(dto.Chunk.ToChunk(), dto.Score);

    /// <summary>Maps a <see cref="Chunk"/> to its wire DTO.</summary>
    public static ChunkDto ToDto(this Chunk chunk) =>
        new(chunk.Id, chunk.Text, chunk.ChunkIndex, chunk.Origin.ToDto(), chunk.Metadata);

    /// <summary>Maps a <see cref="ChunkDto"/> back to a domain <see cref="Chunk"/>.</summary>
    public static Chunk ToChunk(this ChunkDto dto) =>
        new()
        {
            Id = dto.Id,
            Text = dto.Text,
            ChunkIndex = dto.ChunkIndex,
            Origin = new Document.Origin(dto.Origin.SourceId, dto.Origin.DocumentType, dto.Origin.DocumentId),
            Metadata = dto.Metadata
        };

    /// <summary>Maps a <see cref="Document.Origin"/> to its wire DTO.</summary>
    public static OriginDto ToDto(this Document.Origin origin) =>
        new(origin.SourceId, origin.DocumentType, origin.DocumentId);
}
