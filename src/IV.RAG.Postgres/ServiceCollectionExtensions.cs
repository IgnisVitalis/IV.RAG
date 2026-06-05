using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace IV.RAG;

/// <summary>DI registration extensions for IV.RAG.Postgres.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresVectorStore"/> as <see cref="IVectorStore"/> and
    /// <see cref="PostgresRetriever"/> as <see cref="IRetriever"/>,
    /// backed by a pgvector-enabled <see cref="NpgsqlDataSource"/>.
    /// </summary>
    /// <remarks>
    /// The <c>vector</c> PostgreSQL extension must be installed in the target database
    /// before the application starts. Run <c>CREATE EXTENSION IF NOT EXISTS vector</c>
    /// as a superuser during database provisioning or migration.
    /// </remarks>
    public static RAGBuilder AddPostgresVectorStore(
        this RAGBuilder builder,
        Action<PostgresOptions> configure)
    {
        builder.Services.AddOptions<PostgresOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "PostgresOptions.ConnectionString must not be empty.")
            .ValidateOnStart();

        builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            dataSourceBuilder.UseVector();
            return dataSourceBuilder.Build();
        });

        builder.Services.AddSingleton<IVectorStore>(sp => new PostgresVectorStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<IOptions<PostgresOptions>>(),
            sp.GetService<ILogger<PostgresVectorStore>>()));
        builder.Services.AddSingleton<IRetriever, PostgresRetriever>();
        return builder;
    }

    /// <summary>
    /// Registers a <b>keyed</b> vector store (multi-store): a separate <see cref="NpgsqlDataSource"/>,
    /// <see cref="IVectorStore"/>, and <see cref="IRetriever"/> under <paramref name="key"/>, each with
    /// its own table and connection. The store uses a keyed <see cref="IEmbedder"/> registered under the
    /// same key if present, otherwise the default embedder. Resolve with
    /// <c>GetRequiredKeyedService&lt;IVectorStore&gt;(key)</c> / <c>&lt;IRetriever&gt;(key)</c>, or chain
    /// <c>AddKeyedRetrievalPipeline(key)</c> (from <c>IV.RAG.Core</c>) for a per-domain pipeline.
    /// </summary>
    public static RAGBuilder AddPostgresVectorStore(
        this RAGBuilder builder,
        string key,
        Action<PostgresOptions> configure)
    {
        builder.Services.AddOptions<PostgresOptions>(key)
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "PostgresOptions.ConnectionString must not be empty.")
            .ValidateOnStart();

        builder.Services.AddKeyedSingleton<NpgsqlDataSource>(key, (sp, k) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<PostgresOptions>>().Get((string)k!);
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            dataSourceBuilder.UseVector();
            return dataSourceBuilder.Build();
        });

        builder.Services.AddKeyedSingleton<IVectorStore>(key, (sp, k) =>
        {
            var keyStr = (string)k!;
            return new PostgresVectorStore(
                sp.GetRequiredKeyedService<NpgsqlDataSource>(keyStr),
                EmbedderFor(sp, keyStr),
                Options.Create(sp.GetRequiredService<IOptionsMonitor<PostgresOptions>>().Get(keyStr)),
                sp.GetService<ILogger<PostgresVectorStore>>());
        });

        builder.Services.AddKeyedSingleton<IRetriever>(key, (sp, k) =>
        {
            var keyStr = (string)k!;
            return new PostgresRetriever(
                sp.GetRequiredKeyedService<NpgsqlDataSource>(keyStr),
                EmbedderFor(sp, keyStr),
                Options.Create(sp.GetRequiredService<IOptionsMonitor<PostgresOptions>>().Get(keyStr)));
        });
        return builder;
    }

    // A keyed embedder registered under the same key, or the default embedder if none.
    private static IEmbedder EmbedderFor(IServiceProvider sp, string key) =>
        sp.GetKeyedService<IEmbedder>(key) ?? sp.GetRequiredService<IEmbedder>();

    /// <summary>
    /// Registers <see cref="PostgresLexicalRetriever"/> as <see cref="ILexicalRetriever"/>
    /// for use with hybrid retrieval.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="AddPostgresVectorStore"/> to be called first.
    /// The <c>text_search</c> GIN index is created automatically by
    /// <see cref="PostgresVectorStore"/> during the first ingestion.
    /// Chain <c>.AddHybridRetrievalPipeline()</c> to wire everything together.
    /// </remarks>
    public static RAGBuilder AddPostgresLexicalRetriever(this RAGBuilder builder)
    {
        builder.Services.AddSingleton<ILexicalRetriever, PostgresLexicalRetriever>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="PostgresQueryCache"/> as <see cref="IQueryCache"/>.
    /// Requires <see cref="AddPostgresVectorStore"/> to be called first.
    /// Call <c>.AddCachedRetrieval()</c> (from <c>IV.RAG.Core</c>) after this to enable
    /// caching on the retrieval pipeline.
    /// </summary>
    public static RAGBuilder AddPostgresQueryCache(
        this RAGBuilder builder,
        Action<QueryCacheOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure<QueryCacheOptions>(configure);
        builder.Services.AddSingleton<IQueryCache>(sp => new PostgresQueryCache(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<IEmbedder>(),
            sp.GetRequiredService<IOptions<PostgresOptions>>(),
            sp.GetRequiredService<IOptions<QueryCacheOptions>>()));
        return builder;
    }

    /// <summary>
    /// Registers a <see cref="PostgresHealthCheck"/> that verifies the PostgreSQL data source is
    /// reachable. Requires <see cref="AddPostgresVectorStore"/> to have registered the data source.
    /// </summary>
    public static RAGBuilder AddPostgresHealthCheck(this RAGBuilder builder, string name = "postgres")
    {
        builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>(name);
        return builder;
    }
}
