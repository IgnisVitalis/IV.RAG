using System.Text.Json.Serialization;

namespace IV.RAG;

/// <summary>Response body for a remote retrieval query.</summary>
/// <param name="Results">The ranked search results.</param>
public sealed record QueryResponse(
    [property: JsonPropertyName("results")] SearchResultDto[] Results);
