using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IV.RAG;

/// <summary>Health check that verifies the Ollama endpoint is reachable.</summary>
public sealed class OllamaHealthCheck : IHealthCheck
{
    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance using the dedicated Ollama health-check HTTP client.</summary>
    public OllamaHealthCheck(IHttpClientFactory httpClientFactory) =>
        _httpClient = httpClientFactory.CreateClient(ServiceCollectionExtensions.HealthClientName);

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Ollama endpoint returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Ollama is not reachable.", ex);
        }
    }
}
