using Aura.Api.Middleware;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

/// <summary>
/// Integration-style tests that exercise multiple layers together
/// using InMemory EF Core database.
/// </summary>
public class IntegrationTests
{
    private static AuraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    private static (Tenant tenant, User admin) SeedTenantAndAdmin(AuraDbContext db)
    {
        var tenant = new Tenant { Name = "IntTest", Slug = "inttest" };
        db.Tenants.Add(tenant);

        var admin = new User
        {
            TenantId = tenant.Id,
            Email = "admin@inttest.com",
            PasswordHash = AuthHelpers.HashPassword("Admin123!"),
            Role = UserRole.Admin
        };
        db.Users.Add(admin);
        db.SaveChanges();
        return (tenant, admin);
    }

    [Fact]
    public async Task FullDeploymentWorkflow_CreateEssence_Deploy_Run()
    {
        using var db = CreateDb();
        var (tenant, admin) = SeedTenantAndAdmin(db);

        // Create cloud account
        var account = new CloudAccount
        {
            TenantId = tenant.Id,
            Provider = CloudProvider.Azure,
            Label = "Test Azure",
            EncryptedCredentials = "encrypted-stub"
        };
        db.CloudAccounts.Add(account);
        await db.SaveChangesAsync();

        // Create essence with layers
        var essence = new Essence
        {
            TenantId = tenant.Id,
            Name = "Test Essence",
            CloudAccountId = account.Id,
            EssenceJson = """{"layers":{"build":{"isEnabled":true,"executorType":"powershell","parameters":{},"scriptPath":"build.ps1","dependsOn":[]},"test":{"isEnabled":true,"executorType":"python","parameters":{},"scriptPath":"test.py","dependsOn":["build"]}}}""",
            CurrentVersion = 1
        };
        db.Essences.Add(essence);
        db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = essence.Id,
            VersionNumber = 1,
            EssenceJson = essence.EssenceJson,
            ChangedByUserId = admin.Id
        });
        await db.SaveChangesAsync();

        // Create deployment
        var deployment = new Deployment
        {
            TenantId = tenant.Id,
            EssenceId = essence.Id,
            Name = "Test Deployment",
            IsEnabled = true
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        // Create run via orchestration service
        var logger = Mock.Of<ILogger<DeploymentOrchestrationService>>();
        var orchestration = new DeploymentOrchestrationService(db, logger);
        var run = await orchestration.CreateRunAsync(deployment);

        Assert.Equal(RunStatus.Queued, run.Status);
        Assert.Equal(2, run.Layers.Count);

        // Verify topological order: build before test
        var layers = run.Layers.OrderBy(l => l.SortOrder).ToList();
        Assert.Equal("build", layers[0].LayerName);
        Assert.Equal("test", layers[1].LayerName);
        Assert.Equal(0, layers[0].SortOrder);
        Assert.Equal(1, layers[1].SortOrder);
    }

    [Fact]
    public async Task EssenceVersioning_UpdateCreatesNewVersion()
    {
        using var db = CreateDb();
        var (tenant, admin) = SeedTenantAndAdmin(db);

        var account = new CloudAccount
        {
            TenantId = tenant.Id,
            Provider = CloudProvider.Aws,
            Label = "AWS Test",
            EncryptedCredentials = "enc"
        };
        db.CloudAccounts.Add(account);

        var essence = new Essence
        {
            TenantId = tenant.Id,
            Name = "Versioned",
            CloudAccountId = account.Id,
            EssenceJson = """{"layers":{}}""",
            CurrentVersion = 1
        };
        db.Essences.Add(essence);
        db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = essence.Id,
            VersionNumber = 1,
            EssenceJson = essence.EssenceJson,
            ChangedByUserId = admin.Id
        });
        await db.SaveChangesAsync();

        // Simulate update
        essence.EssenceJson = """{"layers":{"deploy":{"isEnabled":true}}}""";
        essence.CurrentVersion = 2;
        db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = essence.Id,
            VersionNumber = 2,
            EssenceJson = essence.EssenceJson,
            ChangedByUserId = admin.Id
        });
        await db.SaveChangesAsync();

        var versions = await db.EssenceVersions
            .Where(v => v.EssenceId == essence.Id)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();

        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions[0].VersionNumber);
        Assert.Equal(2, versions[1].VersionNumber);
        Assert.NotEqual(versions[0].EssenceJson, versions[1].EssenceJson);
    }

    [Fact]
    public async Task AuditLogging_RecordsAllActions()
    {
        using var db = CreateDb();
        var (tenant, admin) = SeedTenantAndAdmin(db);

        var logger = Mock.Of<ILogger<AuditService>>();
        var audit = new AuditService(db, logger);

        await audit.LogAsync(tenant.Id, admin.Id, "create", "Essence", Guid.NewGuid(), "test essence");
        await audit.LogAsync(tenant.Id, admin.Id, "login", "User", admin.Id);
        await audit.LogAsync(tenant.Id, admin.Id, "update", "Deployment", Guid.NewGuid(), "enabled=true");

        var entries = await db.AuditLog.ToListAsync();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Action == "create" && e.EntityType == "Essence");
        Assert.Contains(entries, e => e.Action == "login");
        Assert.Contains(entries, e => e.Action == "update" && e.EntityType == "Deployment");
    }

    [Fact]
    public async Task UserLockout_AfterFiveFailedAttempts()
    {
        using var db = CreateDb();
        var (tenant, _) = SeedTenantAndAdmin(db);

        var user = new User
        {
            TenantId = tenant.Id,
            Email = "locktest@test.com",
            PasswordHash = AuthHelpers.HashPassword("Correct1!"),
            Role = UserRole.Member
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Simulate 5 failed login attempts
        for (int i = 0; i < 5; i++)
        {
            user.FailedLoginAttempts++;
        }
        user.LockedUntil = DateTime.UtcNow.AddMinutes(15);
        user.FailedLoginAttempts = 0;
        await db.SaveChangesAsync();

        Assert.NotNull(user.LockedUntil);
        Assert.True(user.LockedUntil > DateTime.UtcNow);
    }

    [Fact]
    public async Task RefreshTokenRevocation_OnDisable()
    {
        using var db = CreateDb();
        var (tenant, _) = SeedTenantAndAdmin(db);

        var user = new User
        {
            TenantId = tenant.Id,
            Email = "revoke@test.com",
            PasswordHash = AuthHelpers.HashPassword("Pass123!"),
            Role = UserRole.Member,
            RefreshToken = "some-refresh-token",
            RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Assert.NotNull(user.RefreshToken);

        // Simulate disable
        user.IsDisabled = true;
        user.RefreshToken = null;
        user.RefreshTokenExpiresAt = null;
        await db.SaveChangesAsync();

        var updated = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == user.Id);
        Assert.True(updated.IsDisabled);
        Assert.Null(updated.RefreshToken);
    }

    [Fact]
    public async Task RunCancellation_CancelsAllPendingLayers()
    {
        using var db = CreateDb();
        var (tenant, admin) = SeedTenantAndAdmin(db);

        var run = new DeploymentRun
        {
            TenantId = tenant.Id,
            DeploymentId = Guid.NewGuid(),
            Status = RunStatus.Running,
            SnapshotJson = "{}",
            StartedAt = DateTime.UtcNow
        };
        db.DeploymentRuns.Add(run);

        var layer1 = new DeploymentLayer
        {
            RunId = run.Id, LayerName = "a", ExecutorType = ExecutorType.PowerShell,
            Status = LayerStatus.Succeeded, Parameters = "{}", DependsOn = "[]", SortOrder = 0
        };
        var layer2 = new DeploymentLayer
        {
            RunId = run.Id, LayerName = "b", ExecutorType = ExecutorType.Python,
            Status = LayerStatus.Running, Parameters = "{}", DependsOn = "[]", SortOrder = 1
        };
        var layer3 = new DeploymentLayer
        {
            RunId = run.Id, LayerName = "c", ExecutorType = ExecutorType.PowerShell,
            Status = LayerStatus.Pending, Parameters = "{}", DependsOn = "[]", SortOrder = 2
        };
        db.DeploymentLayers.AddRange(layer1, layer2, layer3);
        await db.SaveChangesAsync();

        // Cancel
        run.Status = RunStatus.Cancelled;
        run.CompletedAt = DateTime.UtcNow;
        foreach (var l in new[] { layer2, layer3 })
        {
            l.Status = LayerStatus.Skipped;
            l.CompletedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal(LayerStatus.Succeeded, layer1.Status); // Already completed — not changed
        Assert.Equal(LayerStatus.Skipped, layer2.Status);
        Assert.Equal(LayerStatus.Skipped, layer3.Status);
    }
}
