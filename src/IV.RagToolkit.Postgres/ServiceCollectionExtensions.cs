using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace IV.RagToolkit;

/// <summary>DI registration extensions for IV.RagToolkit.Postgres.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresVectorStore"/> and <see cref="PostgresRetriever"/>
    /// backed by a pgvector-enabled <see cref="NpgsqlDataSource"/>.
    /// </summary>
    /// <remarks>
    /// The <c>vector</c> PostgreSQL extension must be installed in the target database
    /// before the application starts. Run <c>CREATE EXTENSION IF NOT EXISTS vector</c>
    /// as a superuser during database provisioning or migration.
    /// </remarks>
    public static RagToolkitBuilder AddPostgresVectorStore(
        this RagToolkitBuilder builder,
        Action<PostgresOptions> configure)
    {
        builder.Services.Configure<PostgresOptions>(configure);

        builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            dataSourceBuilder.UseVector();
            return dataSourceBuilder.Build();
        });

        builder.Services.AddSingleton<IVectorStore, PostgresVectorStore>();
        builder.Services.AddSingleton<IRetriever, PostgresRetriever>();
        return builder;
    }
}
