using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

public class LlmProviderFactory : ILlmProviderFactory
{
    private readonly Dictionary<string, ILlmProvider> _providers;

    public LlmProviderFactory(IEnumerable<ILlmProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public ILlmProvider GetProvider(string providerName)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
            throw new ArgumentException($"Unsupported LLM provider: {providerName}. Supported: {string.Join(", ", _providers.Keys)}");
        return provider;
    }

    public IReadOnlyList<string> SupportedProviders => _providers.Keys.ToList();
}
