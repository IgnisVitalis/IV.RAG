using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace IV.RAG;

/// <summary>
/// Proxies retrieval to a remote server via HTTP.
/// Implements <see cref="IRetrievalPipeline"/> so it can replace the local retrieval stack
/// without changing pipeline orchestration code.
/// </summary>
public sealed class RemoteRetrievalPipeline : IRetrievalPipeline
{
    private readonly HttpClient _httpClient;
    private readonly string _queryPath;

    /// <summary>Initializes a new instance using a named <c>IV.RAG.Remote.Http</c> HTTP client.</summary>
    public RemoteRetrievalPipeline(IHttpClientFactory httpClientFactory, IOptions<RemoteOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient("IV.RAG.Remote.Http");
        _queryPath = options.Value.QueryPath;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchResult>> QueryAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RetrievalOptions();
        var request = RemoteContract.ToQueryRequest(query, opts);

        var response = await _httpClient.PostAsJsonAsync(_queryPath, request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<QueryResponse>(cancellationToken: cancellationToken);
        return result!.ToSearchResults();
    }
}
