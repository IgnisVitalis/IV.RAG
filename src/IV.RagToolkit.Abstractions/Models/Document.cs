namespace IV.RagToolkit;

/// <summary>Raw input fed into the ingestion pipeline.</summary>
public abstract record Document
{
    /// <summary>Identifies the provenance of this document.</summary>
    public abstract Origin Source { get; }

    /// <summary>Text content of the document.</summary>
    public required string Text { get; init; }

    /// <summary>Arbitrary metadata propagated to all chunks produced from this document.</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Uniquely identifies a document's provenance: the source system, document type within
    /// that system, and the document's own identifier.
    /// </summary>
    public sealed record Origin
    {
        /// <summary>Identifies the source system.</summary>
        public Guid SourceId { get; init; }

        /// <summary>Document type within the source system.</summary>
        public string DocumentType { get; init; }

        /// <summary>Document identifier within the source system.</summary>
        public string DocumentId { get; init; }

        /// <summary>Initializes a new instance and validates that all fields are non-empty.</summary>
        public Origin(Guid sourceId, string documentType, string documentId)
        {
            if (sourceId == Guid.Empty)
                throw new ArgumentException("SourceId must not be empty.", nameof(sourceId));
            if (string.IsNullOrWhiteSpace(documentType))
                throw new ArgumentException("DocumentType must not be null or whitespace.", nameof(documentType));
            if (string.IsNullOrWhiteSpace(documentId))
                throw new ArgumentException("DocumentId must not be null or whitespace.", nameof(documentId));

            SourceId = sourceId;
            DocumentType = documentType;
            DocumentId = documentId;
        }
    }
}
