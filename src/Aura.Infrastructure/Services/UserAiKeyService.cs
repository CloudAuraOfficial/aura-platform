using Aura.Core.Entities;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class UserAiKeyService
{
    private readonly AuraDbContext _db;
    private readonly ICryptoService _crypto;
    private readonly ILogger<UserAiKeyService> _logger;

    public UserAiKeyService(AuraDbContext db, ICryptoService crypto, ILogger<UserAiKeyService> logger)
    {
        _db = db;
        _crypto = crypto;
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

        string plainText;
        try
        {
            plainText = _crypto.Decrypt(provider.EncryptedApiKey);
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

        await UpgradeLegacyCiphertextAsync(provider, plainText, ct);
        return plainText;
    }

    // Re-encrypts a legacy row to the current format after a successful read. Best effort: a failed
    // save must not fail the read, because the legacy value still decrypts. UpdatedAt is left alone
    // because it is shown to the user as "when I last changed this key".
    private async Task UpgradeLegacyCiphertextAsync(UserAiProvider provider, string plainText, CancellationToken ct)
    {
        if (!_crypto.IsLegacyFormat(provider.EncryptedApiKey))
            return;

        try
        {
            provider.EncryptedApiKey = _crypto.Encrypt(plainText);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Re-encrypted legacy AI provider key: user={UserId}, provider={Provider}",
                provider.UserId, provider.ProviderName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Could not re-encrypt legacy AI provider key: user={UserId}, provider={Provider}, error={ErrorType}",
                provider.UserId, provider.ProviderName, ex.GetType().Name);
        }
    }
}
