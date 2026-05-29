namespace IV.RagToolkit;

/// <summary>Raw input fed into the ingestion pipeline.</summary>
public sealed record Document(
    string Text,
    IReadOnlyDictionary<string, object>? Metadata = null);
