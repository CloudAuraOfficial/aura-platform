using Aura.Api.Middleware;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Testcontainers.PostgreSql;
using Xunit;

namespace Aura.Tests;

/// <summary>
/// Integration tests using Testcontainers with real PostgreSQL.
/// These validate EF Core mappings, indexes, query filters, and constraints
/// against a real database — not InMemory.
/// </summary>
public class PostgresIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private AuraDbContext CreateDb(Guid tenantId = default)
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;

        if (tenantId == default)
            return new AuraDbContext(options);

        var tenantCtx = Mock.Of<Core.Interfaces.ITenantContext>(t => t.TenantId == tenantId);
        return new AuraDbContext(options, tenantCtx);
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Create schema
        using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task TenantSlug_UniquenessEnforced()
    {
        using var db = CreateDb();

        db.Tenants.Add(new Tenant { Name = "Acme", Slug = "acme" });
        await db.SaveChangesAsync();

        db.Tenants.Add(new Tenant { Name = "Acme Dupe", Slug = "acme" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task UserEmail_UniquePerTenant()
    {
        using var db = CreateDb();

        var tenant = new Tenant { Name = "T1", Slug = "t1-email" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            TenantId = tenant.Id, Email = "dup@test.com",
            PasswordHash = AuthHelpers.HashPassword("Pass123!"), Role = UserRole.Admin
        });
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            TenantId = tenant.Id, Email = "dup@test.com",
            PasswordHash = AuthHelpers.HashPassword("Pass123!"), Role = UserRole.Member
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task UserEmail_AllowedAcrossTenants()
    {
        using var db = CreateDb();

        var t1 = new Tenant { Name = "T1", Slug = "t1-cross" };
        var t2 = new Tenant { Name = "T2", Slug = "t2-cross" };
        db.Tenants.AddRange(t1, t2);
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            TenantId = t1.Id, Email = "same@test.com",
            PasswordHash = AuthHelpers.HashPassword("Pass123!"), Role = UserRole.Admin
        });
        db.Users.Add(new User
        {
            TenantId = t2.Id, Email = "same@test.com",
            PasswordHash = AuthHelpers.HashPassword("Pass123!"), Role = UserRole.Admin
        });
        await db.SaveChangesAsync(); // Should not throw
    }

    [Fact]
    public async Task TenantQueryFilter_IsolatesData()
    {
        Guid t1Id, t2Id;

        // Seed with unscoped context
        using (var db = CreateDb())
        {
            var t1 = new Tenant { Name = "Iso1", Slug = "iso1" };
            var t2 = new Tenant { Name = "Iso2", Slug = "iso2" };
            db.Tenants.AddRange(t1, t2);
            await db.SaveChangesAsync();
            t1Id = t1.Id;
            t2Id = t2.Id;

            db.Users.Add(new User
            {
                TenantId = t1Id, Email = "a@iso1.com",
                PasswordHash = "h", Role = UserRole.Admin
            });
            db.Users.Add(new User
            {
                TenantId = t2Id, Email = "b@iso2.com",
                PasswordHash = "h", Role = UserRole.Admin
            });
            await db.SaveChangesAsync();
        }

        // Query scoped to t1
        using (var db = CreateDb(t1Id))
        {
            var users = await db.Users.ToListAsync();
            Assert.Single(users);
            Assert.Equal("a@iso1.com", users[0].Email);
        }

        // Query scoped to t2
        using (var db = CreateDb(t2Id))
        {
            var users = await db.Users.ToListAsync();
            Assert.Single(users);
            Assert.Equal("b@iso2.com", users[0].Email);
        }
    }

    [Fact]
    public async Task EssenceVersion_UniquePerEssence()
    {
        using var db = CreateDb();

        var tenant = new Tenant { Name = "V", Slug = "v-unique" };
        db.Tenants.Add(tenant);
        var account = new CloudAccount
        {
            TenantId = tenant.Id, Provider = CloudProvider.Azure,
            Label = "Test", EncryptedCredentials = "enc"
        };
        db.CloudAccounts.Add(account);
        await db.SaveChangesAsync();

        var essence = new Essence
        {
            TenantId = tenant.Id, Name = "E", CloudAccountId = account.Id,
            EssenceJson = "{}", CurrentVersion = 1
        };
        db.Essences.Add(essence);
        await db.SaveChangesAsync();

        db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = essence.Id, VersionNumber = 1,
            EssenceJson = "{}", ChangedByUserId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = essence.Id, VersionNumber = 1,
            EssenceJson = "{}", ChangedByUserId = Guid.NewGuid()
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task JsonbColumns_StoreAndRetrieve()
    {
        using var db = CreateDb();

        var tenant = new Tenant { Name = "Json", Slug = "jsonb-test" };
        db.Tenants.Add(tenant);
        var account = new CloudAccount
        {
            TenantId = tenant.Id, Provider = CloudProvider.Aws,
            Label = "AWS", EncryptedCredentials = "enc"
        };
        db.CloudAccounts.Add(account);
        await db.SaveChangesAsync();

        var json = """{"layers":{"build":{"isEnabled":true,"executorType":"powershell"}}}""";
        var essence = new Essence
        {
            TenantId = tenant.Id, Name = "Jsonb",
            CloudAccountId = account.Id, EssenceJson = json, CurrentVersion = 1
        };
        db.Essences.Add(essence);
        await db.SaveChangesAsync();

        var loaded = await db.Essences.IgnoreQueryFilters()
            .FirstAsync(e => e.Id == essence.Id);
        Assert.Contains("powershell", loaded.EssenceJson);
    }

    [Fact]
    public async Task FullWorkflow_WithRealPostgres()
    {
        using var db = CreateDb();

        // Create tenant + admin
        var tenant = new Tenant { Name = "Full", Slug = "full-wf" };
        db.Tenants.Add(tenant);
        var admin = new User
        {
            TenantId = tenant.Id, Email = "admin@full.com",
            PasswordHash = AuthHelpers.HashPassword("Admin123!"), Role = UserRole.Admin
        };
        db.Users.Add(admin);
        var account = new CloudAccount
        {
            TenantId = tenant.Id, Provider = CloudProvider.Azure,
            Label = "Azure Prod", EncryptedCredentials = "enc-data"
        };
        db.CloudAccounts.Add(account);
        await db.SaveChangesAsync();

        // Create essence
        var essenceJson = """{"layers":{"infra":{"isEnabled":true,"executorType":"powershell","parameters":{},"scriptPath":"infra.ps1","dependsOn":[]},"app":{"isEnabled":true,"executorType":"python","parameters":{},"scriptPath":"app.py","dependsOn":["infra"]}}}""";
        var essence = new Essence
        {
            TenantId = tenant.Id, Name = "Prod Stack",
            CloudAccountId = account.Id, EssenceJson = essenceJson, CurrentVersion = 1
        };
        db.Essences.Add(essence);
        db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = essence.Id, VersionNumber = 1,
            EssenceJson = essenceJson, ChangedByUserId = admin.Id
        });
        await db.SaveChangesAsync();

        // Create deployment and orchestrate run
        var deployment = new Deployment
        {
            TenantId = tenant.Id, EssenceId = essence.Id,
            Name = "Prod Deploy", IsEnabled = true
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        var logger = Mock.Of<ILogger<DeploymentOrchestrationService>>();
        var orchestration = new DeploymentOrchestrationService(db, logger);
        var run = await orchestration.CreateRunAsync(deployment);

        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal(2, run.Layers.Count);

        var layers = run.Layers.OrderBy(l => l.SortOrder).ToList();
        Assert.Equal("infra", layers[0].LayerName);
        Assert.Equal("app", layers[1].LayerName);

        // Audit log
        var auditLogger = Mock.Of<ILogger<AuditService>>();
        var audit = new AuditService(db, auditLogger);
        await audit.LogAsync(tenant.Id, admin.Id, "run_created", "DeploymentRun", run.Id);

        var entries = await db.AuditLog.Where(a => a.TenantId == tenant.Id).ToListAsync();
        Assert.Single(entries);
        Assert.Equal("run_created", entries[0].Action);
    }

    [Fact]
    public async Task InviteToken_UniqueIndex()
    {
        using var db = CreateDb();

        var tenant = new Tenant { Name = "Inv", Slug = "inv-uniq" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        db.Users.Add(new User
        {
            TenantId = tenant.Id, Email = "a@inv.com",
            PasswordHash = "", Role = UserRole.Member,
            InviteToken = token, IsDisabled = true
        });
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            TenantId = tenant.Id, Email = "b@inv.com",
            PasswordHash = "", Role = UserRole.Member,
            InviteToken = token, IsDisabled = true
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task RefreshTokenRevocation_OnPasswordChange()
    {
        using var db = CreateDb();

        var tenant = new Tenant { Name = "Rev", Slug = "rev-pw" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var user = new User
        {
            TenantId = tenant.Id, Email = "pw@rev.com",
            PasswordHash = AuthHelpers.HashPassword("OldPass1!"),
            Role = UserRole.Member,
            RefreshToken = "active-refresh-token",
            RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Simulate password change (as UsersController does)
        user.PasswordHash = AuthHelpers.HashPassword("NewPass1!");
        user.RefreshToken = null;
        user.RefreshTokenExpiresAt = null;
        await db.SaveChangesAsync();

        var updated = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == user.Id);
        Assert.Null(updated.RefreshToken);
        Assert.Null(updated.RefreshTokenExpiresAt);
        Assert.True(AuthHelpers.VerifyPassword("NewPass1!", updated.PasswordHash));
    }
}
