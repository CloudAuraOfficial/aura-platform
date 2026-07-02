using System.Net;
using System.Text;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace Aura.Tests;

// Guard coverage for the non-OpenAI-shaped provider (same never-throw contract
// as OpenAiCompatibleLlmProvider; full guard matrix lives in that suite).
public class AnthropicLlmProviderTests
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
    public async Task GenerateAsync_parses_anthropic_shaped_success()
    {
        const string body = """
        {
          "content": [ { "type": "text", "text": "hello from anthropic" } ],
          "usage": { "input_tokens": 9, "output_tokens": 5 }
        }
        """;
        var result = await new AnthropicLlmProvider(MockClient(HttpStatusCode.OK, body))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-ant-test"));

        Assert.True(result.Success);
        Assert.Equal("hello from anthropic", result.Content);
        Assert.Equal(9, result.InputTokens);
        Assert.Equal(5, result.OutputTokens);
    }

    [Fact]
    public async Task GenerateAsync_returns_failure_on_200_without_content()
    {
        var result = await new AnthropicLlmProvider(
                MockClient(HttpStatusCode.OK, "{\"error\":{\"message\":\"overloaded\"}}"))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-ant-test"));

        Assert.False(result.Success);
        Assert.Contains("no content", result.Error);
    }

    [Fact]
    public async Task GenerateAsync_tolerates_null_usage()
    {
        const string body = """
        { "content": [ { "type": "text", "text": "ok" } ], "usage": null }
        """;
        var result = await new AnthropicLlmProvider(MockClient(HttpStatusCode.OK, body))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-ant-test"));

        Assert.True(result.Success);
        Assert.Equal(0, result.InputTokens);
    }
}
