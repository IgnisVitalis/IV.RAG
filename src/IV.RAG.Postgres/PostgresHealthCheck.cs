using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace IV.RAG;

/// <summary>Health check that verifies the PostgreSQL data source is reachable with a trivial query.</summary>
public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Initializes a new instance over the provided data source.</summary>
    public PostgresHealthCheck(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = _dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", ex);
        }
    }
}
