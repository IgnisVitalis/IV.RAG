using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace IV.RagToolkit.Tests;

public class OllamaEmbedderTests
{
    private static OllamaEmbedder CreateEmbedder(string responseJson, out List<HttpRequestMessage> capturedRequests)
    {
        var requests = new List<HttpRequestMessage>();
        capturedRequests = requests;

        var handler = new MockHttpMessageHandler(responseJson, requests);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IV.RagToolkit.Ollama").Returns(httpClient);

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
        doc.RootElement.GetProperty("input").GetString().Should().Be("test input");
    }

    [Fact]
    public async Task EmbedAsync_ServerReturnsError_Throws()
    {
        var handler = new MockHttpMessageHandler("{}", statusCode: HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IV.RagToolkit.Ollama").Returns(httpClient);

        var embedder = new OllamaEmbedder(factory, Options.Create(new OllamaOptions()));

        var act = async () => await embedder.EmbedAsync("text");

        await act.Should().ThrowAsync<HttpRequestException>();
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
