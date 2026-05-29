using System.Net.Http.Json;
using IV.RagToolkit.Http;
using Microsoft.Extensions.Options;

namespace IV.RagToolkit;

/// <summary>Generates embeddings using the Ollama <c>/api/embed</c> endpoint.</summary>
public sealed class OllamaEmbedder : IEmbedder
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    /// <summary>Initializes a new instance using a named <c>IV.RagToolkit.Ollama</c> HTTP client.</summary>
    public OllamaEmbedder(IHttpClientFactory httpClientFactory, IOptions<OllamaOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient("IV.RagToolkit.Ollama");
        _model = options.Value.EmbeddingModel;
    }

    /// <inheritdoc/>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new EmbedRequest(_model, text);
        var response = await _httpClient.PostAsJsonAsync("/api/embed", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: cancellationToken);
        return result!.Embeddings[0];
    }
}
