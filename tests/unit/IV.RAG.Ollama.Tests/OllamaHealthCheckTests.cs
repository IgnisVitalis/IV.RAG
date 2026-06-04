using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace IV.RAG.Tests;

public class OllamaHealthCheckTests
{
    private static OllamaHealthCheck CreateCheck(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("IV.RAG.Ollama.Health").Returns(httpClient);
        return new OllamaHealthCheck(factory);
    }

    [Fact]
    public async Task CheckHealthAsync_EndpointReachable_ReturnsHealthy()
    {
        var check = CreateCheck(new MockHttpMessageHandler("""{"models":[]}"""));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ServerError_ReturnsUnhealthy()
    {
        var check = CreateCheck(new MockHttpMessageHandler("{}", statusCode: HttpStatusCode.InternalServerError));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
