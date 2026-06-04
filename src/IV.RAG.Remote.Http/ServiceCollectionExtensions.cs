using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace IV.RAG;

/// <summary>DI registration extensions for IV.RAG.Remote.Http.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RemoteRetrievalPipeline"/> as the <see cref="IRetrievalPipeline"/> implementation.
    /// </summary>
    public static RAGBuilder AddRemoteRetrievalPipeline(
        this RAGBuilder builder,
        Action<RemoteOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure<RemoteOptions>(configure);
        else
            builder.Services.AddOptions<RemoteOptions>();

        builder.Services.AddHttpClient("IV.RAG.Remote.Http")
            .ConfigureHttpClient((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<RemoteOptions>>().Value;
                client.BaseAddress = new Uri(options.Endpoint);
                client.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
            })
            .AddStandardResilienceHandler()
            .Configure((resilience, sp) =>
            {
                var seconds = sp.GetRequiredService<IOptions<RemoteOptions>>().Value.TimeoutSeconds;
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(seconds);
                resilience.Retry.MaxRetryAttempts = 1;
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(seconds * 2);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(seconds * 2);
            });

        builder.Services.AddSingleton<IRetrievalPipeline, RemoteRetrievalPipeline>();
        return builder;
    }
}
