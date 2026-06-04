namespace IV.RAG;

/// <summary>Generates vector embeddings for text.</summary>
public interface IEmbedder
{
    /// <summary>Identity of the model used to produce embeddings.</summary>
    EmbedderInfo ModelInfo { get; }

    /// <summary>Returns the embedding vector for <paramref name="text"/>.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns embedding vectors for <paramref name="texts"/> — one per input, in the same order.
    /// The default implementation calls <see cref="EmbedAsync(string, CancellationToken)"/>
    /// sequentially; providers that support native batch embedding should override it.
    /// </summary>
    async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        var embeddings = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            embeddings[i] = await EmbedAsync(texts[i], cancellationToken);
        return embeddings;
    }
}
