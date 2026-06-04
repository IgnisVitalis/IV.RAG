using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> GenerateStreamAsync(
        string query,
        IReadOnlyList<SearchResult> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = BuildContext(chunks);
        var messages = new[]
        {
            new ChatMessage("system", _systemPrompt),
            new ChatMessage("user", $"Context:\n{context}\n\nQuestion: {query}")
        };
        var request = new ChatRequest(_model, messages, Stream: true);

        // ResponseHeadersRead so we start reading the NDJSON stream without buffering the whole body.
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(request) };
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Ollama streams newline-delimited JSON: one object per token, ending with "done": true.
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length == 0) continue;

            var chunk = JsonSerializer.Deserialize<ChatStreamResponse>(line);
            if (chunk?.Message is { Content.Length: > 0 } message)
                yield return message.Content;
            if (chunk?.Done == true)
                yield break;
        }
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
