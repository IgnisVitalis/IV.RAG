using FluentAssertions;
using IV.RAG.IntegrationTests.Fixtures;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace IV.RAG.IntegrationTests;

public sealed class PostgresHealthCheckTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    public PostgresHealthCheckTests(PostgresContainerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CheckHealthAsync_ReachableDataSource_ReturnsHealthy()
    {
        var check = new PostgresHealthCheck(_fixture.DataSource);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_UnreachableDataSource_ReturnsUnhealthy()
    {
        await using var badDataSource =
            new NpgsqlDataSourceBuilder("Host=localhost;Port=1;Database=none;Timeout=1;Command Timeout=1").Build();
        var check = new PostgresHealthCheck(badDataSource);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
