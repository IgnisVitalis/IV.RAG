using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IV.RagToolkit;

/// <summary>DI registration extensions for IV.RagToolkit.Ollama.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OllamaEmbedder"/> as the <see cref="IEmbedder"/> implementation.
    /// </summary>
    public static RagToolkitBuilder AddOllamaEmbedder(
        this RagToolkitBuilder builder,
        Action<OllamaOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure<OllamaOptions>(configure);
        else
            builder.Services.AddOptions<OllamaOptions>();

        builder.Services.AddHttpClient("IV.RagToolkit.Ollama")
            .ConfigureHttpClient((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
                client.BaseAddress = new Uri(options.Endpoint);
            });

        builder.Services.AddSingleton<IEmbedder, OllamaEmbedder>();
        return builder;
    }
}
