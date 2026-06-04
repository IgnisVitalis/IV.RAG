using System.Text.Json.Serialization;

namespace IV.RAG;

/// <summary>Request body for a remote retrieval query.</summary>
/// <param name="Query">The natural-language query.</param>
/// <param name="TopK">Maximum number of results to return.</param>
/// <param name="MinScore">Minimum relevance score for a result to be included.</param>
/// <param name="MetadataFilter">Optional metadata predicate applied during retrieval.</param>
public sealed record QueryRequest(
    [property: JsonPropertyName("query")]          string Query,
    [property: JsonPropertyName("topK")]           int TopK,
    [property: JsonPropertyName("minScore")]       float MinScore,
    [property: JsonPropertyName("metadataFilter")] MetadataFilter? MetadataFilter = null);
