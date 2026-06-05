using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// Wraps the retrieval pipeline with an access-control guard that AND-merges a required
    /// <see cref="MetadataFilter"/> — resolved per query from <paramref name="filterFactory"/> (e.g. the
    /// current tenant) — into every query's options, regardless of what the caller passes.
    /// </summary>
    /// <remarks>
    /// Call this <b>last</b>, after <c>AddCachedRetrieval()</c>, so the guard sits outside the cache and
    /// the required filter becomes part of the cache key — otherwise cached results could be served
    /// across scopes. The factory is invoked per query; for per-request scope, read it from an ambient
    /// accessor (e.g. <c>IHttpContextAccessor</c>) resolved from the provided <see cref="IServiceProvider"/>.
    /// </remarks>
    public static RAGBuilder AddMandatoryRetrievalFilter(
        this RAGBuilder builder,
        Func<IServiceProvider, MetadataFilter?> filterFactory)
    {
        var services = builder.Services;
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IRetrievalPipeline) && !d.IsKeyedService)
            ?? throw new InvalidOperationException(
                "AddMandatoryRetrievalFilter() requires a retrieval pipeline (AddRagToolkit / AddRetrievalPipeline) to be registered first.");

        services.Remove(descriptor);
        services.AddSingleton<IRetrievalPipeline>(sp =>
        {
            var inner = (IRetrievalPipeline)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
            return new GuardedRetrievalPipeline(inner, () => filterFactory(sp));
        });
        return builder;
    }

    /// <summary>
    /// Enables provider-call instrumentation: decorates the registered <see cref="IEmbedder"/> and
    /// <see cref="IGenerator"/> so every embed and generate call emits a span and (for embeds) the
    /// <c>rag.embed_calls</c> counter. Pipeline spans and metrics (ingest, retrieve, cache) are always
    /// emitted via <see cref="RagDiagnostics"/> — this opt-in only adds the cross-provider embed/generate
    /// instrumentation. Call after the provider registrations.
    /// </summary>
    public static RAGBuilder AddRagObservability(this RAGBuilder builder)
    {
        Decorate<IEmbedder>(builder.Services, inner => new InstrumentedEmbedder(inner));
        Decorate<IGenerator>(builder.Services, inner => new InstrumentedGenerator(inner));
        return builder;
    }

    // Replaces the most recent registration of TService with a decorator wrapping the original.
    private static void Decorate<TService>(IServiceCollection services, Func<TService, TService> decorate)
        where TService : class
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor is null) return; // nothing registered to decorate (e.g. no generator on a server)

        services.Remove(descriptor);
        services.AddSingleton(sp =>
        {
            var inner = (TService)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
            return decorate(inner);
        });
    }

    /// <summary>
    /// Registers a <b>keyed</b> retrieval pipeline (multi-store) under <paramref name="key"/>: keyed
    /// <see cref="IIngestionPipeline"/> and <see cref="IRetrievalPipeline"/> backed by the keyed
    /// <see cref="IVectorStore"/> / <see cref="IRetriever"/> (and keyed <see cref="IEmbedder"/> if one is
    /// registered under the same key, otherwise the default), sharing the registered chunker. Resolve
    /// with <c>GetRequiredKeyedService&lt;IRetrievalPipeline&gt;(key)</c> /
    /// <c>&lt;IIngestionPipeline&gt;(key)</c>.
    /// </summary>
    public static RAGBuilder AddKeyedRetrievalPipeline(this RAGBuilder builder, string key)
    {
        builder.Services.AddKeyedSingleton<RetrievalPipeline>(key, (sp, k) =>
        {
            var keyStr = (string)k!;
            return new RetrievalPipeline(
                sp.GetRequiredService<IChunker>(),
                sp.GetKeyedService<IEmbedder>(keyStr) ?? sp.GetRequiredService<IEmbedder>(),
                sp.GetRequiredKeyedService<IVectorStore>(keyStr),
                sp.GetRequiredKeyedService<IRetriever>(keyStr),
                sp.GetService<ILogger<RetrievalPipeline>>() ?? NullLogger<RetrievalPipeline>.Instance);
        });
        builder.Services.AddKeyedSingleton<IIngestionPipeline>(key, (sp, k) => sp.GetRequiredKeyedService<RetrievalPipeline>((string)k!));
        builder.Services.AddKeyedSingleton<IRetrievalPipeline>(key, (sp, k) => sp.GetRequiredKeyedService<RetrievalPipeline>((string)k!));
        return builder;
    }

    /// <summary>
    /// Validates that the registrations required by the configured pipelines are present, throwing a
    /// single <see cref="InvalidOperationException"/> listing everything missing — instead of a cryptic
    /// dependency-injection error when a pipeline is first resolved. Call at the end of setup.
    /// </summary>
    public static RAGBuilder Validate(this RAGBuilder builder)
    {
        var services = builder.Services;
        bool Has(Type serviceType) => services.Any(d => d.ServiceType == serviceType && !d.IsKeyedService);

        var missing = new List<string>();
        var flagged = new HashSet<Type>();
        void Require(Type serviceType, string hint)
        {
            if (!Has(serviceType) && flagged.Add(serviceType))
                missing.Add($"{serviceType.Name} — {hint}");
        }

        // Local ingest + retrieve stack (RetrievalPipeline / full RAG).
        if (Has(typeof(IIngestionPipeline)))
        {
            Require(typeof(IChunker), "register a chunker (e.g. AddSentenceChunker / AddPlainTextChunker)");
            Require(typeof(IEmbedder), "register an embedder (e.g. AddOllamaEmbedder)");
            Require(typeof(IVectorStore), "register a vector store (e.g. AddPostgresVectorStore)");
            Require(typeof(IRetriever), "register a retriever (e.g. AddPostgresVectorStore)");
        }

        // Answer pipelines (full RAG or client) need a generator and a retrieval pipeline.
        if (Has(typeof(IAnswerPipeline)))
        {
            Require(typeof(IGenerator), "register a generator (e.g. AddOllamaGenerator)");
            Require(typeof(IRetrievalPipeline), "register a retrieval pipeline (e.g. AddRagToolkit / AddRemoteRetrievalPipeline)");
        }

        // Lexical (hybrid) retrieval reuses the vector store's data source.
        if (Has(typeof(ILexicalRetriever)))
            Require(typeof(IVectorStore), "AddPostgresLexicalRetriever() requires AddPostgresVectorStore() first");

        if (missing.Count > 0)
            throw new InvalidOperationException(
                "RAG configuration is incomplete — missing registrations:" + Environment.NewLine +
                string.Join(Environment.NewLine, missing.Select(m => "  • " + m)));

        return builder;
    }
}
