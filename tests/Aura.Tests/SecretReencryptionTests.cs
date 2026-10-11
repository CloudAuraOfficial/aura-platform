using Aura.Core.Entities;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

// Lazy re-encryption on read (UserAiKeyService) and the one-off migration (SecretMigrationService),
// run against the real AesCryptoService so the stored formats are the ones production writes.
public class SecretReencryptionTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    // Legacy AES-256-CBC value; see AesCryptoServiceTests for how it was produced.
    private const string LegacyCbcValue = "ABEiM0RVZneImaq7zN3u/84hAm9he171SbwDijRUtv6tsvTwTEs1I5PSbdos/wTe";
    private const string LegacyCbcPlainText = "sk-legacy-cbc-456";

    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static AesCryptoService Crypto() => new(Key);

    private static UserAiKeyService KeyService(AuraDbContext db) =>
        new(db, Crypto(), Mock.Of<ILogger<UserAiKeyService>>());

    private static async Task<UserAiProvider> SeedProvider(AuraDbContext db, string name, string stored)
    {
        var provider = new UserAiProvider
        {
            TenantId = Guid.Empty, // matches the parameterless DbContext's tenant query filter
            UserId = Guid.NewGuid(),
            ProviderName = name,
            EncryptedApiKey = stored,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.UserAiProviders.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    [Fact]
    public async Task Reading_a_legacy_key_returns_the_plaintext_and_rewrites_the_row_to_v2()
    {
        var db = CreateInMemoryDb();
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);

        var key = await KeyService(db).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal(LegacyCbcPlainText, key);
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.StartsWith("v2:", reloaded.EncryptedApiKey);
        Assert.Equal(LegacyCbcPlainText, Crypto().Decrypt(reloaded.EncryptedApiKey));
    }

    [Fact]
    public async Task Lazy_re_encrypt_does_not_change_the_updated_at_timestamp()
    {
        var db = CreateInMemoryDb();
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);

        await KeyService(db).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), reloaded.UpdatedAt);
    }

    [Fact]
    public async Task Reading_a_v2_key_leaves_the_stored_value_untouched()
    {
        var db = CreateInMemoryDb();
        var v2 = Crypto().Encrypt("sk-v2-value");
        var provider = await SeedProvider(db, "openrouter", v2);

        var key = await KeyService(db).GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal("sk-v2-value", key);
        var reloaded = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(v2, reloaded.EncryptedApiKey);
    }

    [Fact]
    public async Task A_failed_lazy_re_encrypt_still_returns_the_key()
    {
        var db = CreateInMemoryDb();
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);
        // Reads succeed; the re-encrypt step fails. The legacy value still decrypts, so the read must not fail.
        var crypto = new Mock<Aura.Core.Interfaces.ICryptoService>();
        crypto.Setup(c => c.Decrypt(LegacyCbcValue)).Returns(LegacyCbcPlainText);
        crypto.Setup(c => c.IsLegacyFormat(LegacyCbcValue)).Returns(true);
        crypto.Setup(c => c.Encrypt(LegacyCbcPlainText)).Throws(new InvalidOperationException("boom"));
        var service = new UserAiKeyService(db, crypto.Object, Mock.Of<ILogger<UserAiKeyService>>());

        var key = await service.GetDecryptedKeyAsync(provider.UserId, "openrouter");

        Assert.Equal(LegacyCbcPlainText, key);
    }

    [Fact]
    public async Task Migration_re_encrypts_legacy_rows_across_tenants_and_reports_counts()
    {
        var db = CreateInMemoryDb();
        var crypto = Crypto();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        db.UserAiProviders.Add(new UserAiProvider { TenantId = tenantA, UserId = Guid.NewGuid(), ProviderName = "a", EncryptedApiKey = LegacyCbcValue });
        db.UserAiProviders.Add(new UserAiProvider { TenantId = tenantB, UserId = Guid.NewGuid(), ProviderName = "b", EncryptedApiKey = crypto.Encrypt("already-v2") });
        db.UserAiProviders.Add(new UserAiProvider { TenantId = tenantB, UserId = Guid.NewGuid(), ProviderName = "c", EncryptedApiKey = "not base64!!" });
        db.CloudAccounts.Add(new CloudAccount { TenantId = tenantA, Label = "legacy-cloud", EncryptedCredentials = LegacyCbcValue });
        await db.SaveChangesAsync();

        var report = await NewMigration(db).MigrateAsync();

        Assert.Equal(new SecretMigrationCounts(Migrated: 1, Current: 1, Unreadable: 1), report.ProviderKeys);
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
        var db = CreateInMemoryDb();
        var provider = await SeedProvider(db, "openrouter", LegacyCbcValue);

        await NewMigration(db).MigrateAsync();
        var afterFirst = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);

        var second = await NewMigration(db).MigrateAsync();

        Assert.Equal(new SecretMigrationCounts(Migrated: 0, Current: 1, Unreadable: 0), second.ProviderKeys);
        var afterSecond = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(afterFirst.EncryptedApiKey, afterSecond.EncryptedApiKey);
    }

    [Fact]
    public async Task Migration_skips_an_unreadable_row_without_aborting_and_leaves_it_as_it_was()
    {
        var db = CreateInMemoryDb();
        var bad = await SeedProvider(db, "bad", "v2:AAAA"); // too short for nonce + tag
        var good = await SeedProvider(db, "good", LegacyCbcValue);

        var report = await NewMigration(db).MigrateAsync();

        Assert.Equal(new SecretMigrationCounts(Migrated: 1, Current: 0, Unreadable: 1), report.ProviderKeys);
        var unchanged = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == bad.Id);
        Assert.Equal("v2:AAAA", unchanged.EncryptedApiKey);
        var migrated = await db.UserAiProviders.AsNoTracking().SingleAsync(p => p.Id == good.Id);
        Assert.StartsWith("v2:", migrated.EncryptedApiKey);
    }

    private static SecretMigrationService NewMigration(AuraDbContext db) =>
        new(db, Crypto(), Mock.Of<ILogger<SecretMigrationService>>());
}
