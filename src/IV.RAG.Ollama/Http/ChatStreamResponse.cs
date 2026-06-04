using System.Text.Json.Serialization;

namespace IV.RAG.Http;

// One line of the Ollama /api/chat streaming (NDJSON) response: a message delta plus a done flag.
internal sealed record ChatStreamResponse(
    [property: JsonPropertyName("message")] ChatMessage? Message,
    [property: JsonPropertyName("done")] bool Done);
