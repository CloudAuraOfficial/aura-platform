using Aura.Core.Entities;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Re-encrypts every legacy (pre-v2) stored secret to the current format. Covers all tenants.
/// Idempotent: a second run finds only v2 rows and changes nothing. A legacy row that cannot be decrypted is counted
/// and left as it is; it does not stop the run. A row the owner changed during the run is left with the owner's value.
/// Only one process runs at a time (see <see cref="ISecretMigrationLock"/>).
/// </summary>
public class SecretMigrationService
{
    private const int BatchSize = 200;

    private readonly AuraDbContext _db;
    private readonly ICryptoService _crypto;
    private readonly StoredSecretService _secrets;
    private readonly ISecretMigrationLock _lock;
    private readonly ILogger<SecretMigrationService> _logger;

    public SecretMigrationService(
        AuraDbContext db,
        ICryptoService crypto,
        StoredSecretService secrets,
        ISecretMigrationLock migrationLock,
        ILogger<SecretMigrationService> logger)
    {
        _db = db;
        _crypto = crypto;
        _secrets = secrets;
        _lock = migrationLock;
        _logger = logger;
    }

    /// <returns>The counts, or null when another process holds the lock and this run did nothing.</returns>
    public async Task<SecretMigrationReport?> MigrateAsync(CancellationToken ct = default)
    {
        if (!await _lock.TryAcquireAsync(ct))
        {
            _logger.LogInformation("Secret migration to v2 skipped: another instance holds the migration lock");
            return null;
        }

        try
        {
            var providerKeys = await MigrateTableAsync<UserAiProvider>(
                _db.UserAiProviders,
                p => p.EncryptedApiKey,
                nameof(UserAiProvider.EncryptedApiKey),
                ct);

            var cloudCredentials = await MigrateTableAsync<CloudAccount>(
                _db.CloudAccounts,
                c => c.EncryptedCredentials,
                nameof(CloudAccount.EncryptedCredentials),
                ct);

            var report = new SecretMigrationReport(providerKeys, cloudCredentials);

            // Counts only. Row values and ciphertext never reach the log.
            _logger.LogInformation(
                "Secret migration to v2 finished: provider keys migrated={ProviderMigrated} current={ProviderCurrent} unreadable={ProviderUnreadable} changedConcurrently={ProviderChanged} failed={ProviderFailed}; cloud credentials migrated={CloudMigrated} current={CloudCurrent} unreadable={CloudUnreadable} changedConcurrently={CloudChanged} failed={CloudFailed}",
                providerKeys.Migrated, providerKeys.Current, providerKeys.Unreadable, providerKeys.ChangedConcurrently, providerKeys.Failed,
                cloudCredentials.Migrated, cloudCredentials.Current, cloudCredentials.Unreadable, cloudCredentials.ChangedConcurrently, cloudCredentials.Failed);

            return report;
        }
        finally
        {
            await _lock.ReleaseAsync(CancellationToken.None);
        }
    }

    private async Task<SecretMigrationCounts> MigrateTableAsync<TEntity>(
        DbSet<TEntity> set,
        Func<TEntity, string> read,
        string column,
        CancellationToken ct)
        where TEntity : BaseEntity
    {
        var migrated = 0;
        var current = 0;
        var unreadable = 0;
        var changedConcurrently = 0;
        var failed = 0;

        // Ids first, so each batch loads a bounded set of rows.
        var ids = await set.IgnoreQueryFilters().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);

        foreach (var batch in ids.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var rows = await set.IgnoreQueryFilters().AsNoTracking().Where(e => batch.Contains(e.Id)).ToListAsync(ct);
            foreach (var row in rows)
            {
                var stored = read(row);
                if (string.IsNullOrEmpty(stored))
                    continue;

                // v2 rows are counted without decrypting. Only legacy values are read.
                if (!_crypto.IsLegacyFormat(stored))
                {
                    current++;
                    continue;
                }

                string plainText;
                try
                {
                    plainText = _crypto.Decrypt(stored);
                }
                catch (StoredSecretUnreadableException)
                {
                    unreadable++;
                    continue;
                }

                switch (await _secrets.TryUpgradeAsync<TEntity>(row.Id, column, stored, plainText, ct))
                {
                    case LegacyUpgradeOutcome.Upgraded:
                        migrated++;
                        break;
                    case LegacyUpgradeOutcome.ChangedConcurrently:
                        changedConcurrently++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }
        }

        return new SecretMigrationCounts(migrated, current, unreadable, changedConcurrently, failed);
    }
}

public sealed record SecretMigrationCounts(
    int Migrated,
    int Current,
    int Unreadable,
    int ChangedConcurrently = 0,
    int Failed = 0);

public sealed record SecretMigrationReport(SecretMigrationCounts ProviderKeys, SecretMigrationCounts CloudCredentials);
