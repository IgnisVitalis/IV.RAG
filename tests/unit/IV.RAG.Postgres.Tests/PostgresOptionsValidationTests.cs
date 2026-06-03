using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace IV.RAG.Tests;

/// <summary>
/// Validation runs inside <c>EnsureSchemaAsync</c> before any database I/O, so these tests
/// exercise it without a live Postgres — the data source is never connected.
/// </summary>
public sealed class PostgresOptionsValidationTests
{
    private static readonly Document.Origin Origin =
        new(new Guid("e1000000-0000-0000-0000-000000000001"), "Test", "doc");

    private static PostgresVectorStore CreateStore(PostgresOptions options)
    {
        var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Database=unused").Build();
        return new PostgresVectorStore(dataSource, Substitute.For<IEmbedder>(), Options.Create(options));
    }

    [Fact]
    public async Task EnsureSchema_HnswMBelowTwo_Throws()
    {
        using var store = CreateStore(new PostgresOptions { VectorIndex = VectorIndexType.Hnsw, HnswM = 1 });

        var act = async () => await store.SetAsync(Origin, []);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("HnswM");
    }

    [Fact]
    public async Task EnsureSchema_EfConstructionBelowTwiceM_Throws()
    {
        using var store = CreateStore(new PostgresOptions
        {
            VectorIndex = VectorIndexType.Hnsw,
            HnswM = 16,
            HnswEfConstruction = 8
        });

        var act = async () => await store.SetAsync(Origin, []);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("HnswEfConstruction");
    }

    [Fact]
    public async Task EnsureSchema_IVFFlatListsBelowOne_Throws()
    {
        using var store = CreateStore(new PostgresOptions { VectorIndex = VectorIndexType.IVFFlat, IVFFlatLists = 0 });

        var act = async () => await store.SetAsync(Origin, []);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("IVFFlatLists");
    }
}
