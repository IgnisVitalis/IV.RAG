using System.Net.Http.Json;
using System.Text;
using IV.RAG.Http;
using Microsoft.Extensions.Options;

namespace IV.RAG;

/// <summary>Generates answers using the Ollama <c>/api/chat</c> endpoint.</summary>
public sealed class OllamaGenerator : IGenerator
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _systemPrompt;

    /// <summary>Initializes a new instance using the Ollama generator HTTP client.</summary>
    public OllamaGenerator(IHttpClientFactory httpClientFactory, IOptions<OllamaOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient(ServiceCollectionExtensions.GeneratorClientName);
        _model = options.Value.GenerationModel;
        _systemPrompt = options.Value.SystemPrompt;
    }

    /// <inheritdoc/>
    public async Task<string> GenerateAsync(
        string query,
        IReadOnlyList<SearchResult> chunks,
        CancellationToken cancellationToken = default)
    {
        var context = BuildContext(chunks);
        var messages = new[]
        {
            new ChatMessage("system", _systemPrompt),
            new ChatMessage("user", $"Context:\n{context}\n\nQuestion: {query}")
        };

        var request = new ChatRequest(_model, messages, Stream: false);
        var response = await _httpClient.PostAsJsonAsync("/api/chat", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: cancellationToken);
        return result!.Message.Content;
    }

    private static string BuildContext(IReadOnlyList<SearchResult> chunks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.AppendLine($"[{i + 1}] {chunks[i].Chunk.Text}");
        }
        return sb.ToString().TrimEnd();
    }
}
