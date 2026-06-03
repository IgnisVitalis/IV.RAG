namespace IV.RAG;

/// <summary>Identifies the embedding model used to produce a vector.</summary>
public sealed record EmbedderInfo(string Provider, string ModelName, int Dimensions)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Provider}/{ModelName} ({Dimensions}d)";
}
