using System.Security.Cryptography;
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

        try
        {
            return _crypto.Decrypt(provider.EncryptedApiKey);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            // Log the exception type only. The message and the stored ciphertext must never reach logs.
            _logger.LogWarning(
                "Stored AI provider key can't be decrypted: user={UserId}, provider={Provider}, error={ErrorType}",
                userId, normalized, ex.GetType().Name);
            throw new StoredKeyUnreadableException(normalized);
        }
    }
}
