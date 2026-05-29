namespace IV.RagToolkit;

/// <summary>Generates vector embeddings for text.</summary>
public interface IEmbedder
{
    /// <summary>Returns the embedding vector for <paramref name="text"/>.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
