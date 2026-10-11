using System.Security.Cryptography;
using Aura.Api.Controllers;
using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Aura.Tests.SecretTestFixtures;

namespace Aura.Tests;

public class CloudAccountsControllerTests
{
    private static async Task<Guid> SeedAccount(AuraDbContext db, string label, string stored = "stored-ciphertext")
    {
        var id = Guid.NewGuid();
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = id, TenantId = Guid.Empty, Provider = CloudProvider.Azure, Label = label,
            EncryptedCredentials = stored
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static CloudAccountsController Controller(AuraDbContext db, ICryptoService crypto, bool upgradeLegacy = false)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Guid.Empty);
        return new CloudAccountsController(db, tenant.Object, crypto, StoredSecrets(db, crypto, upgradeLegacy),
            Mock.Of<ICloudCredentialTester>(), Mock.Of<ILogger<CloudAccountsController>>());
    }

    private static Mock<ICryptoService> UnreadableCrypto()
    {
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(new CryptographicException()));
        return crypto;
    }

    [Fact]
    public async Task Test_names_the_account_when_stored_credentials_cannot_be_read()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var id = await SeedAccount(db, "prod-azure");

        var ex = await Assert.ThrowsAsync<StoredSecretUnreadableException>(() =>
            Controller(db, UnreadableCrypto().Object).Test(id, CancellationToken.None));

        Assert.Equal(
            "The stored credentials for 'prod-azure' can't be read. Re-enter them in Cloud Accounts.", ex.Message);
        Assert.IsType<CryptographicException>(ex.InnerException!.InnerException);
    }

    [Fact]
    public async Task Test_returns_422_through_the_middleware_when_credentials_cannot_be_read()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var id = await SeedAccount(db, "prod-azure");
        var controller = Controller(db, UnreadableCrypto().Object);
        var ctx = new DefaultHttpContext();
        var middleware = new ExceptionHandlerMiddleware(
            async _ => await controller.Test(id, CancellationToken.None),
            Mock.Of<ILogger<ExceptionHandlerMiddleware>>());

        await middleware.InvokeAsync(ctx);

        Assert.Equal(422, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Test_names_the_account_when_the_stored_value_is_empty()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var id = await SeedAccount(db, "empty-row", stored: string.Empty);

        var ex = await Assert.ThrowsAsync<StoredSecretUnreadableException>(() =>
            Controller(db, new AesCryptoService(Key)).Test(id, CancellationToken.None));

        Assert.Contains("'empty-row'", ex.Message);
    }

    [Fact]
    public async Task Test_re_encrypts_a_legacy_credential_to_v2_after_a_successful_read()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var crypto = Crypto();
        var id = await SeedAccount(db, "legacy", LegacyCbcValue);

        await Controller(db, crypto, upgradeLegacy: true).Test(id, CancellationToken.None);

        var stored = await db.CloudAccounts.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.StartsWith("v2:", stored.EncryptedCredentials);
        Assert.Equal(LegacyCbcPlainText, crypto.Decrypt(stored.EncryptedCredentials));
    }

    [Fact]
    public async Task Test_leaves_a_legacy_credential_as_it_is_while_the_upgrade_gate_is_closed()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var id = await SeedAccount(db, "legacy", LegacyCbcValue);
        var tester = new Mock<ICloudCredentialTester>();
        tester.Setup(t => t.TestAsync(It.IsAny<CloudProvider>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CredentialTestResponse(true, "Azure", null, null, 1));

        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Guid.Empty);
        var controller = new CloudAccountsController(db, tenant.Object, Crypto(), StoredSecrets(db, Crypto(), upgradeLegacy: false),
            tester.Object, Mock.Of<ILogger<CloudAccountsController>>());

        await controller.Test(id, CancellationToken.None);

        var stored = await db.CloudAccounts.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(LegacyCbcValue, stored.EncryptedCredentials);
    }

    [Fact]
    public async Task Test_still_returns_the_result_when_saving_the_upgrade_fails()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var id = await SeedAccount(db, "legacy", LegacyCbcValue);
        db.Database.ExecuteSqlRaw(
            "CREATE TRIGGER fail_account_update BEFORE UPDATE ON \"CloudAccounts\" BEGIN SELECT RAISE(ABORT, 'simulated save failure'); END;");

        var result = await Controller(db, Crypto(), upgradeLegacy: true).Test(id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.False(db.ChangeTracker.HasChanges());
        await db.SaveChangesAsync(); // must not replay the failed upgrade
        var stored = await db.CloudAccounts.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(LegacyCbcValue, stored.EncryptedCredentials);
    }

    [Fact]
    public async Task Test_does_not_overwrite_credentials_the_owner_changed_during_the_call()
    {
        var (db, connection) = SqliteDb();
        using var _ = connection;
        var id = await SeedAccount(db, "legacy", LegacyCbcValue);
        var userValue = Crypto().Encrypt("{\"client_secret\":\"new\"}");
        var spy = new SpyCrypto(Crypto(), () => db.Database.ExecuteSqlInterpolated(
            $"UPDATE \"CloudAccounts\" SET \"EncryptedCredentials\" = {userValue} WHERE \"Id\" = {id}"));

        await Controller(db, spy, upgradeLegacy: true).Test(id, CancellationToken.None);

        var stored = await db.CloudAccounts.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(userValue, stored.EncryptedCredentials);
    }
}
