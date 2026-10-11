using System.Security.Cryptography;
using Aura.Api.Controllers;
using Aura.Api.Middleware;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class CloudAccountsControllerTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static async Task<Guid> SeedAccount(AuraDbContext db, string label)
    {
        var id = Guid.NewGuid();
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = id, TenantId = Guid.Empty, Provider = CloudProvider.Azure, Label = label,
            EncryptedCredentials = "stored-ciphertext"
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static CloudAccountsController Controller(AuraDbContext db, Mock<ICryptoService> crypto)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.Setup(t => t.TenantId).Returns(Guid.Empty);
        return new CloudAccountsController(db, tenant.Object, crypto.Object, Mock.Of<ICloudCredentialTester>(),
            Mock.Of<ILogger<CloudAccountsController>>());
    }

    [Fact]
    public async Task Test_names_the_account_when_stored_credentials_cannot_be_read()
    {
        var db = CreateInMemoryDb();
        var id = await SeedAccount(db, "prod-azure");
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(new CryptographicException()));

        var ex = await Assert.ThrowsAsync<StoredSecretUnreadableException>(() =>
            Controller(db, crypto).Test(id, CancellationToken.None));

        Assert.Equal(
            "The stored credentials for 'prod-azure' can't be read. Re-enter them in Cloud Accounts.", ex.Message);
        Assert.IsType<CryptographicException>(ex.InnerException!.InnerException);
    }

    [Fact]
    public async Task Test_returns_422_through_the_middleware_when_credentials_cannot_be_read()
    {
        var db = CreateInMemoryDb();
        var id = await SeedAccount(db, "prod-azure");
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(new CryptographicException()));
        var controller = Controller(db, crypto);
        var ctx = new DefaultHttpContext();
        var middleware = new ExceptionHandlerMiddleware(
            async _ => await controller.Test(id, CancellationToken.None),
            Mock.Of<ILogger<ExceptionHandlerMiddleware>>());

        await middleware.InvokeAsync(ctx);

        Assert.Equal(422, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Test_re_encrypts_a_legacy_credential_to_v2_after_a_successful_read()
    {
        // Legacy AES-256-CBC value for plaintext "sk-legacy-cbc-456" under the key of bytes 1..32.
        const string legacy = "ABEiM0RVZneImaq7zN3u/84hAm9he171SbwDijRUtv6tsvTwTEs1I5PSbdos/wTe";
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var crypto = new AesCryptoService(key);
        var db = CreateInMemoryDb();
        var id = Guid.NewGuid();
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = id, TenantId = Guid.Empty, Provider = CloudProvider.Azure, Label = "legacy",
            EncryptedCredentials = legacy
        });
        await db.SaveChangesAsync();
        var controller = new CloudAccountsController(db, Mock.Of<ITenantContext>(), crypto,
            Mock.Of<ICloudCredentialTester>(), Mock.Of<ILogger<CloudAccountsController>>());

        await controller.Test(id, CancellationToken.None);

        var stored = await db.CloudAccounts.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.StartsWith("v2:", stored.EncryptedCredentials);
        Assert.Equal("sk-legacy-cbc-456", crypto.Decrypt(stored.EncryptedCredentials));
    }
}
