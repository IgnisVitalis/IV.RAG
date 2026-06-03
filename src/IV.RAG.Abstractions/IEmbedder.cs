namespace IV.RAG;

/// <summary>Generates vector embeddings for text.</summary>
public interface IEmbedder
{
    /// <summary>Identity of the model used to produce embeddings.</summary>
    EmbedderInfo ModelInfo { get; }

    /// <summary>Returns the embedding vector for <paramref name="text"/>.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
