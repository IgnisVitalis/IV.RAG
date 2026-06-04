using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace IV.RAG.Tests;

/// <summary>
/// Table names are interpolated into SQL, so they are validated at construction. These tests run
/// without a live database — the data source is never connected.
/// </summary>
public sealed class SqlIdentifierValidationTests
{
    private static NpgsqlDataSource DataSource() =>
        new NpgsqlDataSourceBuilder("Host=localhost;Database=unused").Build();

    private static IOptions<PostgresOptions> Options_(string table = "chunks", string cacheTable = "query_cache") =>
        Options.Create(new PostgresOptions { TableName = table, QueryCacheTableName = cacheTable });

    [Theory]
    [InlineData("chunks; DROP TABLE users")]
    [InlineData("chunks-1")]
    [InlineData("1chunks")]
    [InlineData("ch unks")]
    [InlineData("\"chunks\"")]
    [InlineData("schema.table.extra")]
    [InlineData("")]
    public void VectorStore_InvalidTableName_Throws(string table)
    {
        var act = () => new PostgresVectorStore(DataSource(), Substitute.For<IEmbedder>(), Options_(table: table));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("chunks")]
    [InlineData("my_table")]
    [InlineData("_t")]
    [InlineData("schema.chunks")]
    public void VectorStore_ValidTableName_DoesNotThrow(string table)
    {
        var act = () => new PostgresVectorStore(DataSource(), Substitute.For<IEmbedder>(), Options_(table: table));

        act.Should().NotThrow();
    }

    [Fact]
    public void QueryCache_InvalidTableName_Throws()
    {
        var act = () => new PostgresQueryCache(
            DataSource(), Substitute.For<IEmbedder>(), Options_(cacheTable: "bad;name"), Options.Create(new QueryCacheOptions()));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Retriever_InvalidTableName_Throws()
    {
        var act = () => new PostgresRetriever(DataSource(), Substitute.For<IEmbedder>(), Options_(table: "bad name"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LexicalRetriever_InvalidTableName_Throws()
    {
        var act = () => new PostgresLexicalRetriever(DataSource(), Options_(table: "bad;name"));

        act.Should().Throw<ArgumentException>();
    }
}
