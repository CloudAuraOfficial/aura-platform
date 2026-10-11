using Aura.Core.Entities;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Re-encrypts every legacy (pre-v2) stored secret to the current format. Covers all tenants.
/// Idempotent: rows already in v2 are only verified, so a second run changes nothing. A row that
/// cannot be decrypted is counted and left as it is; it does not stop the run.
/// </summary>
public class SecretMigrationService
{
    private const int BatchSize = 200;

    private readonly AuraDbContext _db;
    private readonly ICryptoService _crypto;
    private readonly ILogger<SecretMigrationService> _logger;

    public SecretMigrationService(AuraDbContext db, ICryptoService crypto, ILogger<SecretMigrationService> logger)
    {
        _db = db;
        _crypto = crypto;
        _logger = logger;
    }

    public async Task<SecretMigrationReport> MigrateAsync(CancellationToken ct = default)
    {
        var providerKeys = await MigrateTableAsync(
            _db.UserAiProviders,
            p => p.EncryptedApiKey,
            (p, value) => p.EncryptedApiKey = value,
            ct);

        var cloudCredentials = await MigrateTableAsync(
            _db.CloudAccounts,
            c => c.EncryptedCredentials,
            (c, value) => c.EncryptedCredentials = value,
            ct);

        var report = new SecretMigrationReport(providerKeys, cloudCredentials);

        // Counts only. Row values and ciphertext never reach the log.
        _logger.LogInformation(
            "Secret migration to v2 finished: provider keys migrated={ProviderMigrated} current={ProviderCurrent} unreadable={ProviderUnreadable}; cloud credentials migrated={CloudMigrated} current={CloudCurrent} unreadable={CloudUnreadable}",
            providerKeys.Migrated, providerKeys.Current, providerKeys.Unreadable,
            cloudCredentials.Migrated, cloudCredentials.Current, cloudCredentials.Unreadable);

        return report;
    }

    private async Task<SecretMigrationCounts> MigrateTableAsync<TEntity>(
        DbSet<TEntity> set,
        Func<TEntity, string> read,
        Action<TEntity, string> write,
        CancellationToken ct)
        where TEntity : BaseEntity
    {
        var migrated = 0;
        var current = 0;
        var unreadable = 0;

        // Ids first, so each batch loads a bounded set of rows and the change tracker is cleared between batches.
        var ids = await set.IgnoreQueryFilters().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);

        foreach (var batch in ids.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var rows = await set.IgnoreQueryFilters().Where(e => batch.Contains(e.Id)).ToListAsync(ct);
            foreach (var row in rows)
            {
                var stored = read(row);
                if (string.IsNullOrEmpty(stored))
                    continue;

                try
                {
                    var plainText = _crypto.Decrypt(stored);
                    if (_crypto.IsLegacyFormat(stored))
                    {
                        write(row, _crypto.Encrypt(plainText));
                        migrated++;
                    }
                    else
                    {
                        current++;
                    }
                }
                catch (StoredSecretUnreadableException)
                {
                    unreadable++;
                }
            }

            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }

        return new SecretMigrationCounts(migrated, current, unreadable);
    }
}

public sealed record SecretMigrationCounts(int Migrated, int Current, int Unreadable);

public sealed record SecretMigrationReport(SecretMigrationCounts ProviderKeys, SecretMigrationCounts CloudCredentials);
