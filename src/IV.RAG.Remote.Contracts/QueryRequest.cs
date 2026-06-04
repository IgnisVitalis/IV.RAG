using System.Text.Json.Serialization;

namespace IV.RAG;

/// <summary>Request body for a remote retrieval query.</summary>
/// <param name="Query">The natural-language query.</param>
/// <param name="TopK">Maximum number of results to return.</param>
/// <param name="MinScore">Minimum relevance score for a result to be included.</param>
/// <param name="MetadataFilter">Optional metadata predicate applied during retrieval.</param>
/// <param name="SourceId">Optional source-system scope (access control).</param>
/// <param name="DocumentType">Optional document-type scope.</param>
/// <param name="DocumentId">Optional document scope.</param>
public sealed record QueryRequest(
    [property: JsonPropertyName("query")]          string Query,
    [property: JsonPropertyName("topK")]           int TopK,
    [property: JsonPropertyName("minScore")]       float MinScore,
    [property: JsonPropertyName("metadataFilter")] MetadataFilter? MetadataFilter = null,
    [property: JsonPropertyName("sourceId")]       Guid? SourceId = null,
    [property: JsonPropertyName("documentType")]   string? DocumentType = null,
    [property: JsonPropertyName("documentId")]     string? DocumentId = null);
