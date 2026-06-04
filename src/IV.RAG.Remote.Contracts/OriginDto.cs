using System.Text.Json.Serialization;

namespace IV.RAG;

/// <summary>Wire representation of a <see cref="Document.Origin"/>.</summary>
/// <param name="SourceId">The source-system identifier.</param>
/// <param name="DocumentType">The document type within the source system.</param>
/// <param name="DocumentId">The document identifier within the source system.</param>
public sealed record OriginDto(
    [property: JsonPropertyName("sourceId")] Guid SourceId,
    [property: JsonPropertyName("documentType")] string DocumentType,
    [property: JsonPropertyName("documentId")] string DocumentId);
