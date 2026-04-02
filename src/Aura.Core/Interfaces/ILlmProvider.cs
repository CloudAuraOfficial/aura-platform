namespace Aura.Core.Interfaces;

public interface ILlmProvider
{
    string ProviderName { get; }
    Task<LlmCompletionResult> GenerateAsync(LlmRequest request, CancellationToken ct = default);
}

public interface ILlmProviderFactory
{
    ILlmProvider GetProvider(string providerName);
    IReadOnlyList<string> SupportedProviders { get; }
}

public record LlmRequest(
    string SystemPrompt,
    string UserPrompt,
    string ApiKey,
    string? Model = null,
    int MaxTokens = 4096
);

public record LlmCompletionResult(
    string Content,
    int InputTokens,
    int OutputTokens,
    string Model,
    bool Success,
    string? Error = null
);
