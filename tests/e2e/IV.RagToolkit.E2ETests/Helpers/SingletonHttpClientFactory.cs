namespace IV.RagToolkit.E2ETests.Helpers;

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> for tests — always returns the same client.
/// </summary>
internal sealed class SingletonHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly HttpClient _client;

    internal SingletonHttpClientFactory(string baseAddress)
        => _client = new HttpClient { BaseAddress = new Uri(baseAddress) };

    public HttpClient CreateClient(string name) => _client;

    public void Dispose() => _client.Dispose();
}
