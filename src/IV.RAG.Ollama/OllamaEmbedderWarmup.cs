using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IV.RAG;

/// <summary>
/// Hosted service that performs a single probe embed at startup so an auto-detected embedding
/// dimension is resolved before any vector-store operation runs — removing the "embed before schema
/// init" ordering caveat. Failures are non-fatal: the application still starts and the dimension is
/// detected on the first real embed instead.
/// </summary>
internal sealed class OllamaEmbedderWarmup : IHostedService
{
    private readonly IEmbedder _embedder;
    private readonly ILogger<OllamaEmbedderWarmup>? _logger;

    public OllamaEmbedderWarmup(IEmbedder embedder, ILogger<OllamaEmbedderWarmup>? logger = null)
    {
        _embedder = embedder;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _embedder.EmbedAsync("warmup", cancellationToken);
            _logger?.LogDebug("Embedder warm-up complete: {Model}.", _embedder.ModelInfo);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Embedder warm-up failed; the embedding dimension will be detected on the first embed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
