using Aura.Api.Services;
using Xunit;

namespace Aura.Tests;

// Base-URL settings (OPENAI_BASE_URL, OPENROUTER_BASE_URL, ANTHROPIC_BASE_URL). A blank value must
// fall back to the public default: an empty variable once became a relative URL, which HttpClient
// rejects with an exception the providers do not handle.
public class LlmEndpointUrlsTests
{
    private const string OpenAiDefault = "https://api.openai.com/v1";
    private const string AnthropicDefault = "https://api.anthropic.com/v1";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChatCompletions_BlankOrUnset_UsesDefault(string? configured)
    {
        Assert.Equal("https://api.openai.com/v1/chat/completions",
            LlmEndpointUrls.ChatCompletions(configured, OpenAiDefault));
    }

    [Theory]
    [InlineData("https://proxy.internal/api/v1", "https://proxy.internal/api/v1/chat/completions")]
    [InlineData("https://proxy.internal/api/v1/", "https://proxy.internal/api/v1/chat/completions")]
    [InlineData("https://proxy.internal/api/v1/chat/completions", "https://proxy.internal/api/v1/chat/completions")]
    [InlineData("  https://proxy.internal/api/v1  ", "https://proxy.internal/api/v1/chat/completions")]
    public void ChatCompletions_AcceptsPrefixOrFullEndpoint(string configured, string expected)
    {
        Assert.Equal(expected, LlmEndpointUrls.ChatCompletions(configured, OpenAiDefault));
    }

    [Theory]
    [InlineData("api.internal/v1")]
    [InlineData("/chat/completions")]
    [InlineData("ftp://files.internal/v1")]
    public void ChatCompletions_NonAbsoluteValue_FailsAtStartup(string configured)
    {
        Assert.Throws<InvalidOperationException>(() => LlmEndpointUrls.ChatCompletions(configured, OpenAiDefault));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnthropicMessages_BlankOrUnset_UsesDefaultMessagesEndpoint(string? configured)
    {
        Assert.Equal("https://api.anthropic.com/v1/messages",
            LlmEndpointUrls.AnthropicMessages(configured, AnthropicDefault));
    }

    [Theory]
    [InlineData("https://gateway.internal/anthropic/v1", "https://gateway.internal/anthropic/v1/messages")]
    [InlineData("https://gateway.internal/anthropic/v1/messages", "https://gateway.internal/anthropic/v1/messages")]
    public void AnthropicMessages_AcceptsPrefixOrFullEndpoint(string configured, string expected)
    {
        Assert.Equal(expected, LlmEndpointUrls.AnthropicMessages(configured, AnthropicDefault));
    }
}
