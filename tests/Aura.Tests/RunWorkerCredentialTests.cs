using System.Security.Cryptography;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class RunWorkerCredentialTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static RunWorkerService Worker() =>
        new(Mock.Of<IServiceScopeFactory>(), Mock.Of<ILogger<RunWorkerService>>());

    private static async Task<(DeploymentRun run, DeploymentLayer layer, Guid accountId)> Seed(AuraDbContext db, string label)
    {
        var accountId = Guid.NewGuid();
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = accountId, TenantId = Guid.Empty, Provider = CloudProvider.Azure, Label = label,
            EncryptedCredentials = "stored-ciphertext"
        });
        await db.SaveChangesAsync();

        var run = new DeploymentRun { Id = Guid.NewGuid(), TenantId = Guid.Empty };
        var layer = new DeploymentLayer { LayerName = "vnet", RunId = run.Id, CloudAccountId = accountId };
        return (run, layer, accountId);
    }

    private static Task<Dictionary<string, string>> Resolve(
        RunWorkerService worker, AuraDbContext db, ICryptoService crypto, IAuditService audit,
        DeploymentRun run, DeploymentLayer layer) =>
        worker.BuildLayerEnvVarsAsync(db, crypto, audit, run, layer,
            new Dictionary<string, string>(), new Dictionary<Guid, Dictionary<string, string>>(),
            new HashSet<Guid>(), CancellationToken.None);

    [Fact]
    public async Task Unreadable_credentials_fail_the_layer_with_an_actionable_reason()
    {
        var db = CreateInMemoryDb();
        var (run, layer, _) = await Seed(db, "prod-azure");
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(new CryptographicException()));

        var ex = await Assert.ThrowsAsync<StoredSecretUnreadableException>(() =>
            Resolve(Worker(), db, crypto.Object, Mock.Of<IAuditService>(), run, layer));

        Assert.Equal(
            "The stored credentials for 'prod-azure' can't be read. Re-enter them in Cloud Accounts.", ex.Message);
    }

    [Fact]
    public async Task Unreadable_credentials_are_not_audited_as_decrypted()
    {
        var db = CreateInMemoryDb();
        var (run, layer, _) = await Seed(db, "prod-azure");
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>()))
            .Throws(StoredSecretUnreadableException.FromCryptoFailure(new CryptographicException()));
        var audit = new Mock<IAuditService>();

        await Assert.ThrowsAsync<StoredSecretUnreadableException>(() =>
            Resolve(Worker(), db, crypto.Object, audit.Object, run, layer));

        audit.Verify(a => a.LogAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Readable_credentials_are_decrypted_and_audited_once()
    {
        var db = CreateInMemoryDb();
        var (run, layer, _) = await Seed(db, "prod-azure");
        var crypto = new Mock<ICryptoService>();
        crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Returns("{\"clientId\":\"abc\"}");
        var audit = new Mock<IAuditService>();

        var envVars = await Resolve(Worker(), db, crypto.Object, audit.Object, run, layer);

        Assert.NotEmpty(envVars);
        audit.Verify(a => a.LogAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), "decrypt_credentials", It.IsAny<string>(),
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
