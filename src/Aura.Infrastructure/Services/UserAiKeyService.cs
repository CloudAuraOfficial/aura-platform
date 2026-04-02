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
        var provider = await _db.UserAiProviders
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ProviderName == providerName, ct);

        if (provider is null || string.IsNullOrEmpty(provider.EncryptedApiKey))
            return null;

        return _crypto.Decrypt(provider.EncryptedApiKey);
    }
}
