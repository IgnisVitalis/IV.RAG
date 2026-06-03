using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IV.RAG;

/// <summary>DI registration extensions for IV.RAG.Core.</summary>
public static class ServiceCollectionExtensions
{
    // Key used to register the unwrapped IRetrievalPipeline so CachedRetrievalPipeline
    // can resolve it without causing a circular dependency.
    internal const string InnerPipelineKey = "retrieval-inner";

    /// <summary>
    /// Registers the full local RAG pipeline (<see cref="IRagPipeline"/>, <see cref="IAnswerPipeline"/>)
    /// backed by <see cref="RetrievalPipeline"/> and <see cref="RagPipeline"/>.
    /// Chain <c>.AddXxxChunker()</c>, <c>.AddXxxEmbedder()</c>, <c>.AddXxxVectorStore()</c>,
    /// and <c>.AddXxxGenerator()</c> to complete the setup.
    /// </summary>
    public static RAGBuilder AddRagToolkit(this IServiceCollection services)
    {
        services.AddSingleton<RetrievalPipeline>();
        services.AddSingleton<IIngestionPipeline>(sp => sp.GetRequiredService<RetrievalPipeline>());
        services.AddKeyedSingleton<IRetrievalPipeline>(InnerPipelineKey, (sp, _) => sp.GetRequiredService<RetrievalPipeline>());
        services.AddSingleton<IRetrievalPipeline>(sp => sp.GetRequiredService<RetrievalPipeline>());
        services.AddSingleton<IRagPipeline, RagPipeline>();
        services.AddSingleton<IAnswerPipeline>(sp => sp.GetRequiredService<IRagPipeline>());
        return new RAGBuilder(services);
    }

    /// <summary>
    /// Registers the server-side retrieval pipeline (<see cref="IIngestionPipeline"/>,
    /// <see cref="IRetrievalPipeline"/>) backed by <see cref="RetrievalPipeline"/>.
    /// Chain <c>.AddXxxChunker()</c>, <c>.AddXxxEmbedder()</c>, and <c>.AddXxxVectorStore()</c>
    /// to complete the setup.
    /// </summary>
    public static RAGBuilder AddRetrievalPipeline(this IServiceCollection services)
    {
        services.AddSingleton<RetrievalPipeline>();
        services.AddSingleton<IIngestionPipeline>(sp => sp.GetRequiredService<RetrievalPipeline>());
        services.AddKeyedSingleton<IRetrievalPipeline>(InnerPipelineKey, (sp, _) => sp.GetRequiredService<RetrievalPipeline>());
        services.AddSingleton<IRetrievalPipeline>(sp => sp.GetRequiredService<RetrievalPipeline>());
        return new RAGBuilder(services);
    }

    /// <summary>
    /// Registers the client-side answer pipeline (<see cref="IAnswerPipeline"/>) backed by
    /// <see cref="AnswerPipeline"/>. Chain <c>.AddXxxRetrievalPipeline()</c> and
    /// <c>.AddXxxGenerator()</c> to complete the setup.
    /// </summary>
    public static RAGBuilder AddAnswerPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IAnswerPipeline, AnswerPipeline>();
        return new RAGBuilder(services);
    }

    /// <summary>
    /// Replaces the default <see cref="IRetrievalPipeline"/> with <see cref="HybridRetrievalPipeline"/>,
    /// combining <see cref="IRetriever"/> (vector search) and <see cref="ILexicalRetriever"/>
    /// via Reciprocal Rank Fusion (RRF). If an <see cref="IReranker"/> is also registered,
    /// it is applied after fusion.
    /// </summary>
    /// <remarks>
    /// Call after the provider registrations (e.g., <c>AddPostgresVectorStore()</c> and
    /// <c>AddPostgresLexicalRetriever()</c>). Ingestion is unaffected and remains on the
    /// original <see cref="RetrievalPipeline"/>.
    /// </remarks>
    public static RAGBuilder AddHybridRetrievalPipeline(
        this RAGBuilder builder,
        Action<HybridRetrievalOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure<HybridRetrievalOptions>(configure);
        builder.Services.AddSingleton<HybridRetrievalPipeline>();
        builder.Services.AddKeyedSingleton<IRetrievalPipeline>(InnerPipelineKey, (sp, _) => sp.GetRequiredService<HybridRetrievalPipeline>());
        builder.Services.AddSingleton<IRetrievalPipeline>(sp => sp.GetRequiredService<HybridRetrievalPipeline>());
        return builder;
    }

    /// <summary>
    /// Registers <see cref="InMemoryQueryCache"/> as <see cref="IQueryCache"/>.
    /// Call <c>.AddCachedRetrieval()</c> after this to enable caching on the retrieval pipeline.
    /// </summary>
    public static RAGBuilder AddInMemoryQueryCache(
        this RAGBuilder builder,
        Action<QueryCacheOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure<QueryCacheOptions>(configure);
        builder.Services.AddSingleton<IQueryCache, InMemoryQueryCache>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="EmbeddingMigrator"/> as <see cref="IEmbeddingMigrator"/>.
    /// Requires <see cref="IVectorStore"/> and <see cref="IEmbedder"/> to be registered.
    /// </summary>
    public static RAGBuilder AddEmbeddingMigrator(this RAGBuilder builder)
    {
        builder.Services.AddSingleton<IEmbeddingMigrator>(sp => new EmbeddingMigrator(
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<IEmbedder>(),
            sp.GetService<ILogger<EmbeddingMigrator>>()));
        return builder;
    }

    /// <summary>
    /// Wraps the current <see cref="IRetrievalPipeline"/> with <see cref="CachedRetrievalPipeline"/>.
    /// Requires a prior <c>AddXxxQueryCache()</c> call to register <see cref="IQueryCache"/>.
    /// Call this last, after all pipeline and cache registrations.
    /// </summary>
    public static RAGBuilder AddCachedRetrieval(this RAGBuilder builder)
    {
        builder.Services.AddSingleton<IRetrievalPipeline>(sp => new CachedRetrievalPipeline(
            sp.GetRequiredKeyedService<IRetrievalPipeline>(InnerPipelineKey),
            sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<IQueryCache>(),
            sp.GetService<ILogger<CachedRetrievalPipeline>>()
        ));
        return builder;
    }
}
