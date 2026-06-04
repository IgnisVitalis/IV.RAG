using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IV.RAG.Tests;

public sealed class PostgresStartupValidationTests
{
    private static IOptions<PostgresOptions> ResolveOptions(Action<PostgresOptions> configure)
    {
        var services = new ServiceCollection();
        new RAGBuilder(services).AddPostgresVectorStore(configure);
        return services.BuildServiceProvider().GetRequiredService<IOptions<PostgresOptions>>();
    }

    [Fact]
    public void AddPostgresVectorStore_EmptyConnectionString_FailsValidation()
    {
        var options = ResolveOptions(o => o.ConnectionString = "");

        var act = () => options.Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddPostgresVectorStore_ValidConnectionString_PassesValidation()
    {
        var options = ResolveOptions(o => o.ConnectionString = "Host=localhost;Database=rag");

        var act = () => options.Value;

        act.Should().NotThrow();
    }
}
