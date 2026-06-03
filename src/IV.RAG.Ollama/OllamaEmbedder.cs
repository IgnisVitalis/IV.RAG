using System.Net.Http.Json;
using IV.RAG.Http;
using Microsoft.Extensions.Options;

namespace IV.RAG;

/// <summary>Generates embeddings using the Ollama <c>/api/embed</c> endpoint.</summary>
public sealed class OllamaEmbedder : IEmbedder
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly string _model;
    private int _detectedDimensions;

    /// <inheritdoc/>
    public EmbedderInfo ModelInfo
    {
        get
        {
            var detected = Volatile.Read(ref _detectedDimensions);
            var dims = detected > 0 ? detected : _options.EmbeddingDimensions;
            return new EmbedderInfo("ollama", _model, dims);
        }
    }

    /// <summary>Initializes a new instance using a named <c>IV.RAG.Ollama</c> HTTP client.</summary>
    public OllamaEmbedder(IHttpClientFactory httpClientFactory, IOptions<OllamaOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient("IV.RAG.Ollama");
        _options = options.Value;
        _model = options.Value.EmbeddingModel;
    }

    /// <inheritdoc/>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = new EmbedRequest(_model, text);
        var response = await _httpClient.PostAsJsonAsync("/api/embed", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: cancellationToken);
        var embedding = result!.Embeddings[0];

        // Detect dimension from first response when not configured explicitly
        if (_options.EmbeddingDimensions == 0)
            Interlocked.CompareExchange(ref _detectedDimensions, embedding.Length, 0);

        return embedding;
    }
}
