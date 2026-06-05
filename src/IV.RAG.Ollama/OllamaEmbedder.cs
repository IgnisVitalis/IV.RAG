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

    /// <summary>Initializes a new instance using the default Ollama embedder HTTP client.</summary>
    public OllamaEmbedder(IHttpClientFactory httpClientFactory, IOptions<OllamaOptions> options)
        : this(httpClientFactory.CreateClient(ServiceCollectionExtensions.EmbedderClientName), options.Value)
    {
    }

    // Used by keyed (multi-store) registration, which resolves a per-key client and named options.
    internal OllamaEmbedder(HttpClient httpClient, OllamaOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _model = options.EmbeddingModel;
    }

    /// <inheritdoc/>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var embeddings = await EmbedAsync([text], cancellationToken);
        return embeddings[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0) return [];

        var batchSize = Math.Max(1, _options.EmbeddingBatchSize);
        var results = new List<float[]>(texts.Count);

        for (var start = 0; start < texts.Count; start += batchSize)
        {
            var length = Math.Min(batchSize, texts.Count - start);
            var slice = new string[length];
            for (var i = 0; i < length; i++)
                slice[i] = texts[start + i];

            var request = new EmbedRequest(_model, slice);
            var response = await _httpClient.PostAsJsonAsync("/api/embed", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: cancellationToken);
            var embeddings = result?.Embeddings;
            if (embeddings is null || embeddings.Length != length)
                throw new InvalidOperationException(
                    $"Ollama '/api/embed' returned {(embeddings is null ? "no" : embeddings.Length.ToString())} embeddings " +
                    $"for {length} input(s) (model '{_model}', endpoint '{_httpClient.BaseAddress}').");

            results.AddRange(embeddings);
        }

        // Detect dimension from the first response when not configured explicitly
        if (_options.EmbeddingDimensions == 0 && results.Count > 0)
            Interlocked.CompareExchange(ref _detectedDimensions, results[0].Length, 0);

        return results;
    }
}
