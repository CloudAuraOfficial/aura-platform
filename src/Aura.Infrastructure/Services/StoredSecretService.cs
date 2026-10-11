using Aura.Core.Entities;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Upgrade gate for owner reads. Set from MIGRATE_SECRETS_TO_V2. Off until the operator has confirmed that
/// every API and Worker process runs code that reads v2, because a v2 row written early is unreadable by old code.
/// </summary>
public sealed class StoredSecretOptions
{
    public bool UpgradeLegacyOnRead { get; init; }
}

public enum LegacyUpgradeOutcome
{
    /// <summary>The row now holds the v2 ciphertext.</summary>
    Upgraded,

    /// <summary>The stored value changed between the read and the write. The newer value is kept.</summary>
    ChangedConcurrently,

    /// <summary>Re-encrypt or save failed. The row is unchanged and the failure was logged by type.</summary>
    Failed
}

/// <summary>
/// The one place that reads a stored secret and re-encrypts a legacy value to v2. Owner reads and the
/// migration both go through here, so the write rules live in one place.
/// </summary>
/// <remarks>
/// The write is a conditional <c>ExecuteUpdate</c> (<c>WHERE Id AND column = storedCiphertext</c>), not a tracked
/// save. A value the user changed after the read is never overwritten with a re-encrypted old value. A failed write
/// leaves nothing in the change tracker, so the caller's next <c>SaveChangesAsync</c> cannot replay it.
/// </remarks>
public sealed class StoredSecretService
{
    private readonly AuraDbContext _db;
    private readonly ICryptoService _crypto;
    private readonly StoredSecretOptions _options;
    private readonly ILogger<StoredSecretService> _logger;

    public StoredSecretService(
        AuraDbContext db,
        ICryptoService crypto,
        StoredSecretOptions options,
        ILogger<StoredSecretService> logger)
    {
        _db = db;
        _crypto = crypto;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Decrypts a stored value that the caller is allowed to read. Throws <see cref="StoredSecretUnreadableException"/>
    /// when it can't be decrypted. When the value is legacy and the gate is open, re-encrypts the row to v2 after the
    /// read. The upgrade is best effort and never fails the read.
    /// </summary>
    /// <param name="id">Row id. The row is read by the caller, so it is already authorized.</param>
    /// <param name="column">Name of the ciphertext property on <typeparamref name="TEntity"/>.</param>
    /// <param name="storedCiphertext">The value as read from that column.</param>
    public async Task<string> ReadAsync<TEntity>(Guid id, string column, string storedCiphertext, CancellationToken ct = default)
        where TEntity : BaseEntity
    {
        var plainText = _crypto.Decrypt(storedCiphertext);

        if (_options.UpgradeLegacyOnRead && _crypto.IsLegacyFormat(storedCiphertext))
        {
            var outcome = await TryUpgradeAsync<TEntity>(id, column, storedCiphertext, plainText, ct);
            if (outcome == LegacyUpgradeOutcome.Upgraded)
                _logger.LogInformation("Re-encrypted legacy stored secret: entity={Entity}, id={Id}", typeof(TEntity).Name, id);
        }

        return plainText;
    }

    /// <summary>
    /// Re-encrypts one value to v2 if it still holds <paramref name="storedCiphertext"/>. Not gated: the migration
    /// calls it after its own flag check. Never throws, except for cancellation.
    /// </summary>
    public async Task<LegacyUpgradeOutcome> TryUpgradeAsync<TEntity>(
        Guid id,
        string column,
        string storedCiphertext,
        string plainText,
        CancellationToken ct = default)
        where TEntity : BaseEntity
    {
        try
        {
            var replacement = _crypto.Encrypt(plainText);

            // Conditional on the value that was read. 0 rows means the owner changed it in between, so their value stays.
            var updated = await _db.Set<TEntity>().IgnoreQueryFilters()
                .Where(e => e.Id == id && EF.Property<string>(e, column) == storedCiphertext)
                .ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<string>(e, column), replacement), ct);

            if (updated == 0)
            {
                _logger.LogInformation(
                    "Legacy stored secret changed concurrently, upgrade skipped: entity={Entity}, id={Id}",
                    typeof(TEntity).Name, id);
                return LegacyUpgradeOutcome.ChangedConcurrently;
            }

            return LegacyUpgradeOutcome.Upgraded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only. The message can carry SQL or key detail.
            _logger.LogWarning(
                "Could not re-encrypt legacy stored secret: entity={Entity}, id={Id}, error={ErrorType}",
                typeof(TEntity).Name, id, ex.GetType().Name);
            return LegacyUpgradeOutcome.Failed;
        }
    }
}
