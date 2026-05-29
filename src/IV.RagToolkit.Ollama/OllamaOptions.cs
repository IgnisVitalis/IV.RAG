namespace IV.RagToolkit;

/// <summary>Configuration for the Ollama provider.</summary>
public sealed class OllamaOptions
{
    /// <summary>Base URL of the Ollama server. Defaults to <c>http://localhost:11434</c>.</summary>
    public string Endpoint { get; init; } = "http://localhost:11434";

    /// <summary>Model used for generating embeddings. Defaults to <c>nomic-embed-text</c>.</summary>
    public string EmbeddingModel { get; init; } = "nomic-embed-text";
}
