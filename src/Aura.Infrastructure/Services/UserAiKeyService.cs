using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Aura.Infrastructure.Services;

public class UserAiKeyService
{
    private readonly AuraDbContext _db;
    private readonly ICryptoService _crypto;

    public UserAiKeyService(AuraDbContext db, ICryptoService crypto)
    {
        _db = db;
        _crypto = crypto;
    }

    public async Task<string?> GetDecryptedKeyAsync(Guid userId, string providerName, CancellationToken ct = default)
    {
        // Provider names are stored lower-cased (AccountSettingsController) and resolved
        // case-insensitively by the factory, but the generate path passes request.Provider
        // verbatim — so match case-insensitively here (the shared chokepoint) instead of
        // relying on every caller to normalize (#20).
        var normalized = providerName.ToLowerInvariant();
        var provider = await _db.UserAiProviders
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ProviderName.ToLower() == normalized, ct);

        if (provider is null || string.IsNullOrEmpty(provider.EncryptedApiKey))
            return null;

        return _crypto.Decrypt(provider.EncryptedApiKey);
    }
}
