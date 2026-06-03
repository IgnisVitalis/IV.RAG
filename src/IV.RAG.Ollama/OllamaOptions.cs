namespace IV.RAG;

/// <summary>Configuration for the Ollama provider.</summary>
public sealed class OllamaOptions
{
    /// <summary>Base URL of the Ollama server. Defaults to <c>http://localhost:11434</c>.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Model used for generating embeddings. Defaults to <c>nomic-embed-text</c>.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Model used for generating answers. Defaults to <c>llama3.2</c>.</summary>
    public string GenerationModel { get; set; } = "llama3.2";

    /// <summary>
    /// System prompt sent to the model before the user message.
    /// Controls the model's role and answer constraints.
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are a helpful assistant. Answer the question using only the provided context. If the context does not contain enough information, say so.";
}
