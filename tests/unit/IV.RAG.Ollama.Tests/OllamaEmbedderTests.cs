using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace IV.RAG.Tests;

public class OllamaEmbedderTests
{
    private static OllamaEmbedder CreateEmbedder(string responseJson, out List<HttpRequestMessage> capturedRequests)
    {
        var requests = new List<HttpRequestMessage>();
        capturedRequests = requests;

        var handler = new MockHttpMessageHandler(responseJson, requests);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IV.RAG.Ollama").Returns(httpClient);

        var options = Options.Create(new OllamaOptions { EmbeddingModel = "nomic-embed-text" });
        return new OllamaEmbedder(factory, options);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsFirstEmbeddingFromResponse()
    {
        var expected = new float[] { 0.1f, 0.2f, 0.3f };
        var responseJson = JsonSerializer.Serialize(new { embeddings = new[] { expected } });

        var embedder = CreateEmbedder(responseJson, out _);

        var result = await embedder.EmbedAsync("hello");

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task EmbedAsync_SendsPostToCorrectPath()
    {
        var responseJson = JsonSerializer.Serialize(new { embeddings = new[] { new float[] { 1f } } });
        var embedder = CreateEmbedder(responseJson, out var requests);

        await embedder.EmbedAsync("hello");

        requests.Single().RequestUri!.PathAndQuery.Should().Be("/api/embed");
        requests.Single().Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task EmbedAsync_SendsCorrectModelAndInput()
    {
        var responseJson = JsonSerializer.Serialize(new { embeddings = new[] { new float[] { 1f } } });
        var embedder = CreateEmbedder(responseJson, out var requests);

        await embedder.EmbedAsync("test input");

        var body = await requests.Single().Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("model").GetString().Should().Be("nomic-embed-text");
        var input = doc.RootElement.GetProperty("input");
        input.ValueKind.Should().Be(JsonValueKind.Array);
        input.EnumerateArray().Select(e => e.GetString()).Should().Equal("test input");
    }

    [Fact]
    public async Task EmbedAsync_ServerReturnsError_Throws()
    {
        var handler = new MockHttpMessageHandler("{}", statusCode: HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IV.RAG.Ollama").Returns(httpClient);

        var embedder = new OllamaEmbedder(factory, Options.Create(new OllamaOptions()));

        var act = async () => await embedder.EmbedAsync("text");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task EmbedAsync_Batch_ReturnsEmbeddingsInInputOrder_InOneRequest()
    {
        var responseJson = JsonSerializer.Serialize(new { embeddings = new[] { new[] { 1f }, new[] { 2f }, new[] { 3f } } });
        var embedder = CreateEmbedder(responseJson, out var requests);

        var result = await embedder.EmbedAsync(new[] { "a", "b", "c" });

        result.Should().HaveCount(3);
        result[0].Should().Equal(1f);
        result[1].Should().Equal(2f);
        result[2].Should().Equal(3f);
        requests.Should().ContainSingle(); // within batch size → single HTTP call
    }

    [Fact]
    public async Task EmbedAsync_Batch_LargerThanBatchSize_SplitsIntoMultipleRequests()
    {
        var requests = new List<HttpRequestMessage>();
        var httpClient = new HttpClient(new EchoCountHttpMessageHandler(requests)) { BaseAddress = new Uri("http://localhost:11434") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IV.RAG.Ollama").Returns(httpClient);
        var embedder = new OllamaEmbedder(factory, Options.Create(new OllamaOptions { EmbeddingBatchSize = 2 }));

        var result = await embedder.EmbedAsync(new[] { "a", "b", "c", "d", "e" });

        result.Should().HaveCount(5);
        requests.Should().HaveCount(3); // ceil(5 / 2)
    }

    [Fact]
    public async Task EmbedAsync_Batch_ResponseCountMismatch_Throws()
    {
        var responseJson = JsonSerializer.Serialize(new { embeddings = new[] { new[] { 1f } } }); // 1 vector for 2 inputs
        var embedder = CreateEmbedder(responseJson, out _);

        var act = async () => await embedder.EmbedAsync(new[] { "a", "b" });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task EmbedAsync_EmptyBatch_ReturnsEmpty_WithoutCallingServer()
    {
        var embedder = CreateEmbedder("{}", out var requests);

        var result = await embedder.EmbedAsync(Array.Empty<string>());

        result.Should().BeEmpty();
        requests.Should().BeEmpty();
    }
}

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseContent;
    private readonly HttpStatusCode _statusCode;
    private readonly List<HttpRequestMessage>? _capturedRequests;

    internal MockHttpMessageHandler(
        string responseContent,
        List<HttpRequestMessage>? capturedRequests = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseContent = responseContent;
        _capturedRequests = capturedRequests;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _capturedRequests?.Add(request);
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent, Encoding.UTF8, "application/json")
        });
    }
}

// Returns one embedding per input in the request, so sub-batching can be observed via request count.
internal sealed class EchoCountHttpMessageHandler : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests;

    internal EchoCountHttpMessageHandler(List<HttpRequestMessage> requests) => _requests = requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var count = JsonDocument.Parse(body).RootElement.GetProperty("input").GetArrayLength();
        var embeddings = Enumerable.Range(0, count).Select(_ => new[] { 0.5f }).ToArray();
        var json = JsonSerializer.Serialize(new { embeddings });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
