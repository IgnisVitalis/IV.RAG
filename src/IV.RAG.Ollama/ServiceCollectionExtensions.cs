using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace IV.RAG;

/// <summary>DI registration extensions for IV.RAG.Ollama.</summary>
public static class ServiceCollectionExtensions
{
    // Embedding and generation use separate named clients so they can carry independent timeouts
    // (generation is far slower than embedding).
    internal const string EmbedderClientName = "IV.RAG.Ollama.Embedder";
    internal const string GeneratorClientName = "IV.RAG.Ollama.Generator";

    /// <summary>
    /// Registers <see cref="OllamaEmbedder"/> as the <see cref="IEmbedder"/> implementation.
    /// </summary>
    public static RAGBuilder AddOllamaEmbedder(
        this RAGBuilder builder,
        Action<OllamaOptions>? configure = null)
    {
        builder.Services.AddOptions<OllamaOptions>()
            .Configure(configure ?? (_ => { }))
            .Validate(o => Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _), "OllamaOptions.Endpoint must be an absolute URI.")
            .ValidateOnStart();

        builder.Services.AddHttpClient(EmbedderClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
                client.BaseAddress = new Uri(options.Endpoint);
                client.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
            })
            .AddStandardResilienceHandler()
            .Configure((resilience, sp) =>
            {
                var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
                ConfigureResilience(resilience, options.EmbeddingTimeoutSeconds, maxRetries: 2, retryOnTimeout: true);
            });

        builder.Services.AddSingleton<IEmbedder, OllamaEmbedder>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="OllamaGenerator"/> as the <see cref="IGenerator"/> implementation.
    /// </summary>
    public static RAGBuilder AddOllamaGenerator(
        this RAGBuilder builder,
        Action<OllamaOptions>? configure = null)
    {
        builder.Services.AddOptions<OllamaOptions>()
            .Configure(configure ?? (_ => { }))
            .Validate(o => Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _), "OllamaOptions.Endpoint must be an absolute URI.")
            .ValidateOnStart();

        builder.Services.AddHttpClient(GeneratorClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
                client.BaseAddress = new Uri(options.Endpoint);
                client.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
            })
            .AddStandardResilienceHandler()
            .Configure((resilience, sp) =>
            {
                var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
                // Generation is expensive and slow: long timeout, and don't retry on timeout
                // (re-running a slow generation only wastes work). The handler requires at least
                // one retry attempt, so transient connection/5xx failures still get one retry.
                ConfigureResilience(resilience, options.GenerationTimeoutSeconds, maxRetries: 1, retryOnTimeout: false);
            });

        builder.Services.AddSingleton<IGenerator, OllamaGenerator>();
        return builder;
    }

    // Aligns the standard resilience handler with a single per-attempt timeout while keeping its
    // internal validation satisfied: MaxRetryAttempts >= 1, TotalRequestTimeout > AttemptTimeout,
    // and the circuit-breaker sampling window >= 2 x AttemptTimeout.
    private static void ConfigureResilience(
        HttpStandardResilienceOptions resilience, int attemptTimeoutSeconds, int maxRetries, bool retryOnTimeout)
    {
        resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(attemptTimeoutSeconds);
        resilience.Retry.MaxRetryAttempts = maxRetries;
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(attemptTimeoutSeconds * (maxRetries + 1));
        resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(attemptTimeoutSeconds * 2);

        if (!retryOnTimeout)
        {
            // The handler requires >= 1 retry, but a per-attempt timeout shouldn't trigger one.
            var standardShouldHandle = resilience.Retry.ShouldHandle;
            resilience.Retry.ShouldHandle = args =>
                args.Outcome.Exception is Polly.Timeout.TimeoutRejectedException
                    ? Polly.PredicateResult.False()
                    : standardShouldHandle(args);
        }
    }
}
