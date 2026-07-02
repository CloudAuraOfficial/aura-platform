using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aura.Tests;

public class EssenceVersioningTests
{
    private sealed record FakeTenant(Guid TenantId) : ITenantContext;

    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static AuraDbContext CreateInMemoryDb(string dbName, Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AuraDbContext(options, new FakeTenant(tenantId));
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

    // Regression for the essence-version cross-tenant IDOR: GetVersion/DiffVersions must gate
    // on the tenant-filtered Essences set, because EssenceVersions itself has no tenant filter.
    [Fact]
    public async Task EssenceVersion_ParentEssence_HiddenFromOtherTenant_ButVersionRowIsUnfiltered()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid essenceId;
        using (var seed = CreateInMemoryDb(dbName, tenantA))
        {
            var essence = new Essence
            {
                TenantId = tenantA,
                Name = "SecretInfra",
                CloudAccountId = Guid.NewGuid(),
                EssenceJson = """{"layers":{}}""",
                CurrentVersion = 1
            };
            seed.Essences.Add(essence);
            seed.EssenceVersions.Add(new EssenceVersion
            {
                EssenceId = essence.Id,
                VersionNumber = 1,
                EssenceJson = """{"secret":"tenantA"}""",
                ChangedByUserId = Guid.NewGuid()
            });
            await seed.SaveChangesAsync();
            essenceId = essence.Id;
        }

        // A context scoped to tenant B must NOT see tenant A's essence (this is the guard).
        using var asB = CreateInMemoryDb(dbName, tenantB);
        Assert.False(await asB.Essences.AnyAsync(e => e.Id == essenceId));

        // The version row itself is unfiltered — proving the parent-gate is load-bearing:
        // without it, tenant B could read tenant A's version JSON directly.
        var leaked = await asB.EssenceVersions
            .FirstOrDefaultAsync(v => v.EssenceId == essenceId && v.VersionNumber == 1);
        Assert.NotNull(leaked);
    }
}
