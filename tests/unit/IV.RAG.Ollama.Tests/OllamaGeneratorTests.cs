using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace IV.RAG.Tests;

public class OllamaGeneratorTests
{
    private static readonly Document.Origin TestOrigin =
        new(new Guid("a0000000-0000-0000-0000-000000000001"), "Test", "doc-1");

    private static OllamaGenerator CreateGenerator(string responseJson, out List<HttpRequestMessage> capturedRequests, OllamaOptions? options = null)
    {
        var requests = new List<HttpRequestMessage>();
        capturedRequests = requests;

        var handler = new MockHttpMessageHandler(responseJson, requests);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IV.RAG.Ollama.Generator").Returns(httpClient);

        return new OllamaGenerator(factory, Options.Create(options ?? new OllamaOptions()));
    }

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content } });

    [Fact]
    public async Task GenerateAsync_ReturnsModelResponseContent()
    {
        var generator = CreateGenerator(ChatResponse("The answer is 42."), out _);

        var result = await generator.GenerateAsync("question", []);

        result.Should().Be("The answer is 42.");
    }

    [Fact]
    public async Task GenerateAsync_SendsPostToCorrectPath()
    {
        var generator = CreateGenerator(ChatResponse("ok"), out var requests);

        await generator.GenerateAsync("question", []);

        requests.Single().RequestUri!.PathAndQuery.Should().Be("/api/chat");
        requests.Single().Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GenerateAsync_SendsConfiguredModel()
    {
        var options = new OllamaOptions { GenerationModel = "mistral" };
        var generator = CreateGenerator(ChatResponse("ok"), out var requests, options);

        await generator.GenerateAsync("question", []);

        var body = await requests.Single().Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("model").GetString().Should().Be("mistral");
    }

    [Fact]
    public async Task GenerateAsync_SendsConfiguredSystemPrompt()
    {
        var options = new OllamaOptions { SystemPrompt = "Custom instructions." };
        var generator = CreateGenerator(ChatResponse("ok"), out var requests, options);

        await generator.GenerateAsync("question", []);

        var body = await requests.Single().Content!.ReadAsStringAsync();
        var messages = JsonDocument.Parse(body).RootElement.GetProperty("messages");
        var systemMessage = messages.EnumerateArray().First(m => m.GetProperty("role").GetString() == "system");
        systemMessage.GetProperty("content").GetString().Should().Be("Custom instructions.");
    }

    [Fact]
    public async Task GenerateAsync_IncludesChunkTextInUserMessage()
    {
        var chunk = new Chunk { Text = "Paris is the capital of France.", Origin = TestOrigin };
        var results = new[] { new SearchResult(chunk, 0.9f) };
        var generator = CreateGenerator(ChatResponse("Paris."), out var requests);

        await generator.GenerateAsync("What is the capital of France?", results);

        var body = await requests.Single().Content!.ReadAsStringAsync();
        var messages = JsonDocument.Parse(body).RootElement.GetProperty("messages");
        var userMessage = messages.EnumerateArray().First(m => m.GetProperty("role").GetString() == "user");
        userMessage.GetProperty("content").GetString().Should().Contain("Paris is the capital of France.");
    }

    [Fact]
    public async Task GenerateAsync_ServerReturnsError_Throws()
    {
        var handler = new MockHttpMessageHandler("{}", statusCode: HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IV.RAG.Ollama.Generator").Returns(httpClient);

        var generator = new OllamaGenerator(factory, Options.Create(new OllamaOptions()));

        var act = async () => await generator.GenerateAsync("question", []);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GenerateStreamAsync_YieldsMessageContentDeltas_InOrder()
    {
        var ndjson = string.Join("\n",
            """{"message":{"role":"assistant","content":"Hello"},"done":false}""",
            """{"message":{"role":"assistant","content":" world"},"done":false}""",
            """{"message":{"role":"assistant","content":""},"done":true}""");
        var generator = CreateGenerator(ndjson, out _);

        var fragments = new List<string>();
        await foreach (var fragment in generator.GenerateStreamAsync("q", []))
            fragments.Add(fragment);

        fragments.Should().Equal("Hello", " world");
    }

    [Fact]
    public async Task GenerateStreamAsync_RequestsStreamingMode()
    {
        var ndjson = """{"message":{"role":"assistant","content":"x"},"done":true}""";
        var generator = CreateGenerator(ndjson, out var requests);

        await foreach (var _ in generator.GenerateStreamAsync("q", [])) { }

        var body = await requests.Single().Content!.ReadAsStringAsync();
        JsonDocument.Parse(body).RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
    }
}
