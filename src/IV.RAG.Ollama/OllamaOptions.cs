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
    /// Per-attempt HTTP timeout, in seconds, for embedding requests. Defaults to <c>100</c>.
    /// Embedding requests are retried on transient failures within this budget.
    /// </summary>
    public int EmbeddingTimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// Per-attempt HTTP timeout, in seconds, for generation requests. Generation is typically far
    /// slower than embedding, so this defaults to <c>600</c>. Generation requests are not retried on
    /// timeout (re-running a slow generation only wastes work).
    /// </summary>
    public int GenerationTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Dimensionality of the vectors produced by <see cref="EmbeddingModel"/>.
    /// Leave at <c>0</c> (the default) to detect the dimension automatically from the first
    /// embed response. Set explicitly only when a store operation (e.g. migration check) must
    /// run before any embed call has been made, or to pin a specific truncated dimension.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 0;

    /// <summary>
    /// Maximum number of texts sent to the Ollama <c>/api/embed</c> endpoint in a single request
    /// when embedding in batch. Larger values reduce HTTP round-trips; smaller values bound request
    /// size and memory. Batches larger than this are split into multiple requests automatically.
    /// Defaults to <c>32</c>.
    /// </summary>
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>
    /// Maximum number of characters of retrieved context included in the generation prompt. Chunks
    /// are added highest-ranked first until the budget is reached; lower-ranked chunks are then
    /// dropped (with a debug log). The top-ranked chunk is always included even if it alone exceeds
    /// the budget. <c>0</c> (the default) means no limit.
    /// </summary>
    public int MaxContextChars { get; set; } = 0;

    /// <summary>
    /// System prompt sent to the model before the user message.
    /// Controls the model's role and answer constraints.
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are a helpful assistant. Answer the question using only the provided context. If the context does not contain enough information, say so.";
}
