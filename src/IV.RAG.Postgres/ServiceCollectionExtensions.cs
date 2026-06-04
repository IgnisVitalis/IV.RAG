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
}
