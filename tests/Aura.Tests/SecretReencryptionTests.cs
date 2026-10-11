using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static Aura.Tests.SecretTestFixtures;

namespace Aura.Tests;

// Lazy re-encryption on owner reads (UserAiKeyService) and the one-off migration (SecretMigrationService),
// run against the real AesCryptoService so the stored formats are the ones production writes. SQLite is used
// because the conditional upgrade is an ExecuteUpdate, which the in-memory provider does not support.
public class SecretReencryptionTests
{
    private static async Task<UserAiProvider> SeedProvider(AuraDbContext db, string name, string stored, Guid? tenant = null)
    {
        var provider = new UserAiProvider
        {
            TenantId = tenant ?? Guid.Empty, // matches the parameterless DbContext's tenant query filter
            UserId = Guid.NewGuid(),
            ProviderName = name,
            EncryptedApiKey = stored,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.UserAiProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    private static UserAiKeyService KeyService(AuraDbContext db, ICryptoService crypto, bool upgradeLegacy = true) =>
        new(db, StoredSecrets(db, crypto, upgradeLegacy), Log<UserAiKeyService>());

    private static SecretMigrationService NewMigration(AuraDbContext db, ICryptoService crypto, ISecretMigrationLock? migrationLock = null) =>
        new(db, crypto, StoredSecrets(db, crypto), migrationLock ?? new FakeMigrationLock(), Log<SecretMigrationService>());

    [Fact]
    public async Task Reading_a_legacy_key_returns_the_plaintext_and_rewrites_the_row_to_v2()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);

        var key = await KeyService(db, Crypto()).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal(LegacyCbcPlainText, key);
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.StartsWith("v2:", reloaded.EncryptedApiKey);
        Assert.Equal(LegacyCbcPlainText, Crypto().Decrypt(reloaded.EncryptedApiKey));
    }

    [Fact]
    public async Task Lazy_re_encrypt_does_not_change_the_updated_at_timestamp()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);

        await KeyService(db, Crypto()).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), reloaded.UpdatedAt);
    }

    [Fact]
    public async Task Reading_a_v2_key_leaves_the_stored_value_untouched()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var v2 = Crypto().Encrypt("sk-v2-value");
        var provider = await SeedProvider(db, "openrouter", v2);

        var key = await KeyService(db, Crypto()).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal("sk-v2-value", key);
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(v2, reloaded.EncryptedApiKey);
    }

    [Fact]
    public async Task Reading_a_legacy_key_with_the_upgrade_gate_closed_leaves_the_row_as_it_is()
    {
        // Gate closed: the operator has not yet confirmed that every process reads v2.
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);

        var key = await KeyService(db, Crypto(), upgradeLegacy: false).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal(LegacyCbcPlainText, key);
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(LegacyCbcValue, reloaded.EncryptedApiKey);
    }

    [Fact]
    public async Task A_failed_lazy_re_encrypt_still_returns_the_key()
    {
        var db = InMemoryDb();
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);
        // Reads succeed; the re-encrypt step fails. The legacy value still decrypts, so the read must not fail.
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(LegacyCbcValue)).Returns(LegacyCbcPlainText);
        crypto.Setup(c => c.IsLegacyFormat(LegacyCbcValue)).Returns(true);
        crypto.Setup(c => c.Encrypt(LegacyCbcPlainText)).Throws(new InvalidOperationException("boom"));

        var key = await KeyService(db, crypto.Object).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal(LegacyCbcPlainText, key);
    }

    [Fact]
    public async Task A_failed_save_on_the_lazy_upgrade_still_returns_the_key_and_leaves_no_pending_change()
    {
        // The save itself fails. Before the conditional write, a failed SaveChanges left the entity Modified
        // and the caller's next SaveChangesAsync replayed it and threw.
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);
        db.Database.ExecuteSqlRaw(
            "CREATE TRIGGER fail_provider_update BEFORE UPDATE ON \"UserAiProviders\" BEGIN SELECT RAISE(ABORT, 'simulated save failure'); END;");

        var key = await KeyService(db, Crypto()).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal(LegacyCbcPlainText, key);
        Assert.False(db.ChangeTracker.HasChanges());
        await db.SaveChangesAsync(); // must not replay the failed upgrade

        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(LegacyCbcValue, reloaded.EncryptedApiKey);
    }

    [Fact]
    public async Task A_key_the_user_changed_during_the_upgrade_is_not_overwritten_by_the_old_value()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);
        var userValue = Crypto().Encrypt("sk-user-edit");
        // The user saves a new key between the read and the conditional write.
        var spy = new SpyCrypto(Crypto(), () => db.Database.ExecuteSqlInterpolated(
            $"UPDATE \"UserAiProviders\" SET \"EncryptedApiKey\" = {userValue} WHERE \"Id\" = {provider.Id}"));

        var key = await KeyService(db, spy).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal(LegacyCbcPlainText, key); // the value that was read is still returned
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(userValue, reloaded.EncryptedApiKey);
        Assert.Equal("sk-user-edit", Crypto().Decrypt(reloaded.EncryptedApiKey));
    }

    [Fact]
    public async Task Migration_re_encrypts_legacy_rows_across_tenants_and_reports_counts()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var crypto = Crypto();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await SeedProvider(db, "a", LegacyCbcValue, tenantA);
        await SeedProvider(db, "b", crypto.Encrypt("already-v2"), tenantB);
        await SeedProvider(db, "c", "not base64!!", tenantB);
        db.CloudAccounts.Add(new CloudAccount { TenantId = tenantA, Label = "legacy-cloud", EncryptedCredentials = LegacyCbcValue });
        await db.SaveChangesAsync();

        var report = await NewMigration(db, crypto).MigrateAsync();

        Assert.NotNull(report);
        Assert.Equal(new SecretMigrationCounts(Migrated: 1, Current: 1, Unreadable: 1), report!.ProviderKeys);
        Assert.Equal(new SecretMigrationCounts(Migrated: 1, Current: 0, Unreadable: 0), report.CloudCredentials);

        var providers = await db.UserAiProviders.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(LegacyCbcPlainText, crypto.Decrypt(providers.Single(p => p.ProviderName == "a").EncryptedApiKey));
        Assert.StartsWith("v2:", providers.Single(p => p.ProviderName == "a").EncryptedApiKey);
        Assert.Equal("not base64!!", providers.Single(p => p.ProviderName == "c").EncryptedApiKey);

        var cloud = await db.CloudAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.StartsWith("v2:", cloud.EncryptedCredentials);
    }

    [Fact]
    public async Task Migration_is_idempotent_and_a_second_run_changes_nothing()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);

        await NewMigration(db, Crypto()).MigrateAsync();
        var afterFirst = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);

        var second = await NewMigration(db, Crypto()).MigrateAsync();

        Assert.Equal(new SecretMigrationCounts(Migrated: 0, Current: 1, Unreadable: 0), second!.ProviderKeys);
        var afterSecond = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(afterFirst.EncryptedApiKey, afterSecond.EncryptedApiKey);
    }

    [Fact]
    public async Task Migration_skips_v2_rows_without_decrypting_them()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var v2 = Crypto().Encrypt("already-v2");
        await SeedProvider(db, "v2-row", v2);
        var spy = new SpyCrypto(Crypto());

        var report = await NewMigration(db, spy).MigrateAsync();

        Assert.Equal(new SecretMigrationCounts(Migrated: 0, Current: 1, Unreadable: 0), report!.ProviderKeys);
        Assert.DoesNotContain(v2, spy.DecryptedInputs);
    }

    [Fact]
    public async Task Migration_skips_an_unreadable_legacy_row_without_aborting_and_leaves_it_as_it_was()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var bad = await SeedProvider(db, "bad", "not base64!!"); // no ':' so it is treated as legacy, then fails to decrypt
        var good = await SeedProvider(db, "good", LegacyCbcValue);

        var report = await NewMigration(db, Crypto()).MigrateAsync();

        Assert.Equal(new SecretMigrationCounts(Migrated: 1, Current: 0, Unreadable: 1), report!.ProviderKeys);
        var unchanged = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == bad.Id);
        Assert.Equal("not base64!!", unchanged.EncryptedApiKey);
        var migrated = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == good.Id);
        Assert.StartsWith("v2:", migrated.EncryptedApiKey);
    }

    [Fact]
    public async Task Migration_does_not_overwrite_a_key_the_user_changed_during_the_run()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);
        var userValue = Crypto().Encrypt("sk-user-edit");
        var spy = new SpyCrypto(Crypto(), () => db.Database.ExecuteSqlInterpolated(
            $"UPDATE \"UserAiProviders\" SET \"EncryptedApiKey\" = {userValue} WHERE \"Id\" = {provider.Id}"));

        var report = await NewMigration(db, spy).MigrateAsync();

        Assert.Equal(new SecretMigrationCounts(Migrated: 0, Current: 0, Unreadable: 0, ChangedConcurrently: 1), report!.ProviderKeys);
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(userValue, reloaded.EncryptedApiKey);
    }

    [Fact]
    public async Task Migration_counts_a_row_whose_save_fails_and_leaves_it_unchanged()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);
        db.Database.ExecuteSqlRaw(
            "CREATE TRIGGER fail_provider_update BEFORE UPDATE ON \"UserAiProviders\" BEGIN SELECT RAISE(ABORT, 'simulated save failure'); END;");

        var report = await NewMigration(db, Crypto()).MigrateAsync();

        Assert.Equal(new SecretMigrationCounts(Migrated: 0, Current: 0, Unreadable: 0, Failed: 1), report!.ProviderKeys);
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(LegacyCbcValue, reloaded.EncryptedApiKey);
    }

    [Fact]
    public async Task Migration_does_nothing_when_another_instance_holds_the_lock()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);
        var heldElsewhere = new FakeMigrationLock(acquire: false);

        var report = await NewMigration(db, Crypto(), heldElsewhere).MigrateAsync();

        Assert.Null(report);
        Assert.Equal(0, heldElsewhere.Releases);
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(LegacyCbcValue, reloaded.EncryptedApiKey);
    }

    [Fact]
    public async Task Migration_releases_the_lock_when_the_run_is_cancelled()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        await SeedProvider(db, "openrouter", LegacyCbcValue);
        var migrationLock = new FakeMigrationLock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewMigration(db, Crypto(), migrationLock).MigrateAsync(cts.Token));

        Assert.Equal(1, migrationLock.Releases);
    }
}
