using Aura.Core.Entities;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aura.Tests;

public class EssenceVersioningTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    [Fact]
    public async Task EssenceVersion_CanBeCreatedAndQueried()
    {
        var db = CreateInMemoryDb();

        var tenant = new Tenant { Name = "Test", Slug = "test" };
        db.Tenants.Add(tenant);

        var account = new CloudAccount
        {
            TenantId = tenant.Id,
            Provider = Core.Enums.CloudProvider.Azure,
            Label = "Test",
            EncryptedCredentials = "enc"
        };
        db.CloudAccounts.Add(account);

        var essence = new Essence
        {
            TenantId = tenant.Id,
            Name = "TestEssence",
            CloudAccountId = account.Id,
            EssenceJson = """{"layers":{}}""",
            CurrentVersion = 1
        };
        db.Essences.Add(essence);

        db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = essence.Id,
            VersionNumber = 1,
            EssenceJson = """{"layers":{}}""",
            ChangedByUserId = Guid.NewGuid()
        });

        await db.SaveChangesAsync();

        var versions = await db.EssenceVersions
            .Where(v => v.EssenceId == essence.Id)
            .ToListAsync();

        Assert.Single(versions);
        Assert.Equal(1, versions[0].VersionNumber);
    }

    [Fact]
    public async Task EssenceVersion_MultipleVersions_OrderedCorrectly()
    {
        var db = CreateInMemoryDb();

        var essenceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        for (int i = 1; i <= 3; i++)
        {
            db.EssenceVersions.Add(new EssenceVersion
            {
                EssenceId = essenceId,
                VersionNumber = i,
                EssenceJson = $"{{\"version\":{i}}}",
                ChangedByUserId = userId
            });
        }

        await db.SaveChangesAsync();

        var versions = await db.EssenceVersions
            .Where(v => v.EssenceId == essenceId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync();

        Assert.Equal(3, versions.Count);
        Assert.Equal(3, versions[0].VersionNumber);
        Assert.Equal(1, versions[2].VersionNumber);
    }
}
