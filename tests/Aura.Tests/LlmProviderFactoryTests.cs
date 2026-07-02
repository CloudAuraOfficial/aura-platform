using Aura.Core.Interfaces;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

// Factory resolution previously had zero test coverage (PR #19 review note).
public class LlmProviderFactoryTests
{
    private static LlmProviderFactory Factory()
    {
        var http = new HttpClient();
        return new LlmProviderFactory(new ILlmProvider[]
        {
            new OpenAiCompatibleLlmProvider(http, "openai", "https://api.openai.com/v1/chat/completions", "gpt-4o"),
            new AnthropicLlmProvider(http),
            new OpenAiCompatibleLlmProvider(http, "openrouter", "https://openrouter.ai/api/v1/chat/completions", "openai/gpt-4o")
        });
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("openrouter")]
    [InlineData("OpenRouter")] // resolution is case-insensitive
    [InlineData("OPENAI")]
    public void GetProvider_resolves_supported_names(string name)
    {
        var provider = Factory().GetProvider(name);
        Assert.Equal(name.ToLowerInvariant(), provider.ProviderName.ToLowerInvariant());
    }

    [Fact]
    public void GetProvider_throws_on_unknown_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => Factory().GetProvider("mistral"));
        Assert.Contains("Unsupported LLM provider", ex.Message);
    }

    [Fact]
    public void SupportedProviders_lists_all_registrations()
    {
        var supported = Factory().SupportedProviders;
        Assert.Equal(3, supported.Count);
        Assert.Contains("openai", supported);
        Assert.Contains("anthropic", supported);
        Assert.Contains("openrouter", supported);
    }
}
