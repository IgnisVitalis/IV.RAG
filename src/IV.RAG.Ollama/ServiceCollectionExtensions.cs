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
    internal const string HealthClientName = "IV.RAG.Ollama.Health";

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

        AddEmbedderClient(builder.Services, EmbedderClientName,
            sp => sp.GetRequiredService<IOptions<OllamaOptions>>().Value);

        builder.Services.AddSingleton<IEmbedder, OllamaEmbedder>();
        return builder;
    }

    /// <summary>
    /// Registers a <b>keyed</b> <see cref="OllamaEmbedder"/> (multi-store): a separate
    /// <see cref="IEmbedder"/>, options, and HTTP client under <paramref name="key"/>, so different
    /// domains can use different embedding models. Resolve with
    /// <c>GetRequiredKeyedService&lt;IEmbedder&gt;(key)</c>; keyed vector stores registered under the
    /// same key pick it up automatically.
    /// </summary>
    public static RAGBuilder AddOllamaEmbedder(
        this RAGBuilder builder,
        string key,
        Action<OllamaOptions>? configure = null)
    {
        builder.Services.AddOptions<OllamaOptions>(key)
            .Configure(configure ?? (_ => { }))
            .Validate(o => Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _), "OllamaOptions.Endpoint must be an absolute URI.")
            .ValidateOnStart();

        AddEmbedderClient(builder.Services, $"{EmbedderClientName}.{key}",
            sp => sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(key));

        builder.Services.AddKeyedSingleton<IEmbedder>(key, (sp, k) =>
        {
            var keyStr = (string)k!;
            var options = sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(keyStr);
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient($"{EmbedderClientName}.{keyStr}");
            return new OllamaEmbedder(client, options);
        });
        return builder;
    }

    private static void AddEmbedderClient(
        IServiceCollection services, string clientName, Func<IServiceProvider, OllamaOptions> getOptions)
    {
        services.AddHttpClient(clientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var options = getOptions(sp);
                client.BaseAddress = new Uri(options.Endpoint);
                client.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
            })
            .AddStandardResilienceHandler()
            .Configure((resilience, sp) =>
                ConfigureResilience(resilience, getOptions(sp).EmbeddingTimeoutSeconds, maxRetries: 2, retryOnTimeout: true));
    }

    /// <summary>
    /// Registers a startup warm-up (hosted service) that performs one probe embed so an auto-detected
    /// embedding dimension is resolved before the first vector-store operation, removing the
    /// embed-before-schema ordering caveat. Opt-in: it touches Ollama at host startup, but warm-up
    /// failures are non-fatal (the dimension is detected on the first real embed instead). Warms the
    /// default <see cref="IEmbedder"/>.
    /// </summary>
    public static RAGBuilder AddOllamaEmbedderWarmup(this RAGBuilder builder)
    {
        builder.Services.AddHostedService<OllamaEmbedderWarmup>();
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

    /// <summary>
    /// Registers an <see cref="OllamaHealthCheck"/> that verifies the Ollama endpoint is reachable.
    /// Uses a dedicated short-timeout HTTP client (no resilience handler) so the probe fails fast.
    /// </summary>
    public static RAGBuilder AddOllamaHealthCheck(this RAGBuilder builder, string name = "ollama")
    {
        builder.Services.AddHttpClient(HealthClientName)
            .ConfigureHttpClient((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
                client.BaseAddress = new Uri(options.Endpoint);
                client.Timeout = TimeSpan.FromSeconds(5);
            });
        builder.Services.AddHealthChecks().AddCheck<OllamaHealthCheck>(name);
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
