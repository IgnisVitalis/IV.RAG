using Microsoft.Extensions.DependencyInjection;

namespace IV.RagToolkit;

/// <summary>DI registration extensions for IV.RagToolkit.Core.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core RAG pipeline and returns a builder for chaining provider registrations.
    /// </summary>
    public static RagToolkitBuilder AddRagToolkit(this IServiceCollection services)
    {
        services.AddSingleton<IRagPipeline, RagPipeline>();
        return new RagToolkitBuilder(services);
    }

    /// <summary>
    /// Registers <see cref="FixedSizeChunker"/> as the <see cref="IChunker"/> implementation.
    /// </summary>
    public static RagToolkitBuilder AddFixedSizeChunker(
        this RagToolkitBuilder builder,
        Action<FixedSizeChunkerOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure<FixedSizeChunkerOptions>(configure);
        else
            builder.Services.AddOptions<FixedSizeChunkerOptions>();

        builder.Services.AddSingleton<IChunker, FixedSizeChunker>();
        return builder;
    }
}
