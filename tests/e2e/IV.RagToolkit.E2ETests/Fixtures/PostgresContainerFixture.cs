using Npgsql;
using Pgvector;
using Testcontainers.PostgreSql;

namespace IV.RagToolkit.E2ETests.Fixtures;

/// <summary>
/// Starts a pgvector-enabled Postgres container once per test class.
/// Each test should use a unique table name for isolation.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .Build();

    private NpgsqlDataSource? _dataSource;

    public NpgsqlDataSource DataSource => _dataSource!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Install the extension using a plain connection BEFORE building the typed
        // data source. NpgsqlDataSource caches the DB type catalog on first connect —
        // if vector isn't installed yet, Vector parameters will fail for the lifetime
        // of that data source.
        await using var setup = new NpgsqlConnection(_container.GetConnectionString());
        await setup.OpenAsync();
        await using var cmd = setup.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
        await cmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlDataSourceBuilder(_container.GetConnectionString());
        builder.UseVector();
        _dataSource = builder.Build();
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();

        await _container.DisposeAsync();
    }

    /// <summary>Returns a unique table name safe for use in SQL identifiers.</summary>
    public static string NewTable() => $"t_{Guid.NewGuid():N}";
}
