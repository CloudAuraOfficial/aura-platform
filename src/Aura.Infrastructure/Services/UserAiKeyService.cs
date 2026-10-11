using Aura.Core.Entities;
using Aura.Core.Exceptions;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class UserAiKeyService
{
    private readonly AuraDbContext _db;
    private readonly StoredSecretService _secrets;
    private readonly ILogger<UserAiKeyService> _logger;

    public UserAiKeyService(AuraDbContext db, StoredSecretService secrets, ILogger<UserAiKeyService> logger)
    {
        _db = db;
        _secrets = secrets;
        _logger = logger;
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

        try
        {
            // Decrypts, and re-encrypts a legacy row to v2 when the upgrade gate is open (StoredSecretService).
            return await _secrets.ReadAsync<UserAiProvider>(
                provider.Id, nameof(UserAiProvider.EncryptedApiKey), provider.EncryptedApiKey, ct);
        }
        catch (StoredSecretUnreadableException ex)
        {
            // Log the failure type only. The message and the stored ciphertext must never reach logs.
            _logger.LogWarning(
                "Stored AI provider key can't be decrypted: user={UserId}, provider={Provider}, error={ErrorType}",
                userId, normalized, ex.InnerException?.GetType().Name ?? nameof(StoredSecretUnreadableException));
            // Name the provider as the caller selected it, so the message matches what the user picked.
            throw StoredSecretUnreadableException.ForProviderKey(providerName, ex);
        }
    }
}
