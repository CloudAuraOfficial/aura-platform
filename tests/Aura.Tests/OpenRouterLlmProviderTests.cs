using System.Net;
using System.Text;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace Aura.Tests;

public class OpenRouterLlmProviderTests
{
    private static HttpClient MockClient(HttpStatusCode status, string body)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        return new HttpClient(handler.Object);
    }

    [Fact]
    public void ProviderName_is_openrouter()
    {
        var provider = new OpenRouterLlmProvider(MockClient(HttpStatusCode.OK, "{}"));
        Assert.Equal("openrouter", provider.ProviderName);
    }

    [Fact]
    public async Task GenerateAsync_parses_openai_shaped_success()
    {
        const string body = """
        {
          "choices": [ { "message": { "content": "hello from openrouter" } } ],
          "usage": { "prompt_tokens": 11, "completion_tokens": 7 }
        }
        """;
        var provider = new OpenRouterLlmProvider(MockClient(HttpStatusCode.OK, body));

        var result = await provider.GenerateAsync(
            new LlmRequest("sys", "user", "sk-test", Model: "openai/gpt-4o"));

        Assert.True(result.Success);
        Assert.Equal("hello from openrouter", result.Content);
        Assert.Equal(11, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Equal("openai/gpt-4o", result.Model);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task GenerateAsync_returns_failure_on_error_status()
    {
        var provider = new OpenRouterLlmProvider(
            MockClient(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}"));

        var result = await provider.GenerateAsync(new LlmRequest("sys", "user", "sk-bad"));

        Assert.False(result.Success);
        Assert.Equal("", result.Content);
        Assert.NotNull(result.Error);
        Assert.Contains("401", result.Error);
    }

    [Fact]
    public async Task GenerateAsync_falls_back_to_default_model_when_unspecified()
    {
        const string body = """
        { "choices": [ { "message": { "content": "x" } } ] }
        """;
        var provider = new OpenRouterLlmProvider(MockClient(HttpStatusCode.OK, body));

        var result = await provider.GenerateAsync(new LlmRequest("sys", "user", "sk-test"));

        Assert.True(result.Success);
        Assert.Equal("openai/gpt-4o", result.Model); // namespaced default
    }
}
