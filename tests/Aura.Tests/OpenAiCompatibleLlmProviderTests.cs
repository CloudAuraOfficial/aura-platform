using System.Net;
using System.Text;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace Aura.Tests;

public class OpenAiCompatibleLlmProviderTests
{
    private static HttpClient MockClient(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
        return new HttpClient(handler.Object);
    }

    private static HttpClient ThrowingClient(Exception ex)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(ex);
        return new HttpClient(handler.Object);
    }

    private static OpenAiCompatibleLlmProvider OpenRouter(HttpClient http) =>
        new(http, "openrouter", "https://openrouter.ai/api/v1/chat/completions", "openai/gpt-4o");

    [Fact]
    public void ProviderName_comes_from_registration()
    {
        Assert.Equal("openrouter", OpenRouter(MockClient(HttpStatusCode.OK, "{}")).ProviderName);
        Assert.Equal("openai", new OpenAiCompatibleLlmProvider(
            MockClient(HttpStatusCode.OK, "{}"), "openai", "https://api.openai.com/v1/chat/completions", "gpt-4o").ProviderName);
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
        var result = await OpenRouter(MockClient(HttpStatusCode.OK, body)).GenerateAsync(
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
        var result = await OpenRouter(MockClient(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}"))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-bad"));

        Assert.False(result.Success);
        Assert.Equal("", result.Content);
        Assert.Contains("401", result.Error);
    }

    [Fact]
    public async Task GenerateAsync_falls_back_to_default_model_when_unspecified()
    {
        const string body = """
        { "choices": [ { "message": { "content": "x" } } ] }
        """;
        var result = await OpenRouter(MockClient(HttpStatusCode.OK, body))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-test"));

        Assert.True(result.Success);
        Assert.Equal("openai/gpt-4o", result.Model); // namespaced default
    }

    // F1: OpenRouter documents HTTP 200 + {"error":{...}} for moderation/upstream failures.
    [Fact]
    public async Task GenerateAsync_returns_failure_on_200_with_error_body()
    {
        var result = await OpenRouter(MockClient(HttpStatusCode.OK,
                "{\"error\":{\"code\":502,\"message\":\"upstream failed\"}}"))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-test"));

        Assert.False(result.Success);
        Assert.Contains("no choices", result.Error);
    }

    // F2: null/empty content (refusal, truncation) must fail, not succeed with "".
    [Fact]
    public async Task GenerateAsync_returns_failure_on_null_content()
    {
        const string body = """
        { "choices": [ { "message": { "content": null }, "finish_reason": "content_filter" } ] }
        """;
        var result = await OpenRouter(MockClient(HttpStatusCode.OK, body))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-test"));

        Assert.False(result.Success);
        Assert.Contains("empty completion", result.Error);
    }

    // F4: a proxy serving a 200 HTML interstitial must not throw JsonException.
    [Fact]
    public async Task GenerateAsync_returns_failure_on_non_json_200()
    {
        var result = await OpenRouter(MockClient(HttpStatusCode.OK, "<html>captive portal</html>", "text/html"))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-test"));

        Assert.False(result.Success);
        Assert.Contains("unparseable", result.Error);
    }

    // F5: trailing newline in a stored key is trimmed; embedded control chars fail cleanly.
    [Fact]
    public async Task GenerateAsync_trims_trailing_newline_in_api_key()
    {
        const string body = """
        { "choices": [ { "message": { "content": "ok" } } ] }
        """;
        var result = await OpenRouter(MockClient(HttpStatusCode.OK, body))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-test\n"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task GenerateAsync_returns_failure_on_malformed_api_key()
    {
        var result = await OpenRouter(MockClient(HttpStatusCode.OK, "{}"))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-bad\nkey"));

        Assert.False(result.Success);
        Assert.Contains("API key", result.Error);
    }

    // F7: explicit "usage": null (multi-upstream gateways) must not discard the completion.
    [Fact]
    public async Task GenerateAsync_tolerates_null_or_partial_usage()
    {
        const string body = """
        { "choices": [ { "message": { "content": "ok" } } ], "usage": null }
        """;
        var result = await OpenRouter(MockClient(HttpStatusCode.OK, body))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-test"));

        Assert.True(result.Success);
        Assert.Equal(0, result.InputTokens);
        Assert.Equal(0, result.OutputTokens);
    }

    // Transport/timeout must return a failed result; user cancellation must propagate.
    [Fact]
    public async Task GenerateAsync_returns_failure_on_transport_error_and_timeout()
    {
        var transport = await OpenRouter(ThrowingClient(new HttpRequestException("connection refused")))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-test"));
        Assert.False(transport.Success);
        Assert.Contains("request failed", transport.Error);

        var timeout = await OpenRouter(ThrowingClient(new TaskCanceledException("timed out")))
            .GenerateAsync(new LlmRequest("sys", "user", "sk-test"));
        Assert.False(timeout.Success);
    }

    [Fact]
    public async Task GenerateAsync_propagates_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = OpenRouter(ThrowingClient(new TaskCanceledException("canceled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GenerateAsync(new LlmRequest("sys", "user", "sk-test"), cts.Token));
    }
}
