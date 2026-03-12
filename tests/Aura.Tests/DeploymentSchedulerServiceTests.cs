using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class DeploymentSchedulerServiceTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IDeploymentOrchestrationService> _orchestrationMock;
    private readonly string _dbName;

    public DeploymentSchedulerServiceTests()
    {
        _orchestrationMock = new Mock<IDeploymentOrchestrationService>();
        _dbName = $"SchedulerTest_{Guid.NewGuid()}";

        var services = new ServiceCollection();

        services.AddDbContext<AuraDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(_dbName);
        });

        services.AddSingleton<ITenantContext>(new WorkerTenantContext());
        services.AddSingleton<IDeploymentOrchestrationService>(_orchestrationMock.Object);
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    private AuraDbContext CreateDb()
    {
        var scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AuraDbContext>();
    }

    private async Task<(Guid tenantId, Guid essenceId)> SeedTenantAndEssenceAsync(string slug)
    {
        using var db = CreateDb();
        var tenantId = Guid.NewGuid();
        var essenceId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = slug });
        db.Essences.Add(new Essence
        {
            Id = essenceId,
            TenantId = tenantId,
            Name = "TestEssence",
            EssenceJson = "{\"layers\":{}}"
        });
        await db.SaveChangesAsync();
        return (tenantId, essenceId);
    }

    [Fact]
    public async Task EvaluateDue_MatchingCron_CreatesRun()
    {
        var (tenantId, essenceId) = await SeedTenantAndEssenceAsync("test-match");

        var deploymentId = Guid.NewGuid();
        using (var db = CreateDb())
        {
            db.Deployments.Add(new Deployment
            {
                Id = deploymentId,
                TenantId = tenantId,
                EssenceId = essenceId,
                Name = "EveryMinute",
                CronExpression = "* * * * *",
                IsEnabled = true
            });
            await db.SaveChangesAsync();
        }

        _orchestrationMock
            .Setup(o => o.CreateRunAsync(It.IsAny<Deployment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentRun
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DeploymentId = deploymentId,
                Status = RunStatus.Queued,
                SnapshotJson = "{}"
            });

        var scheduler = CreateScheduler();

        await scheduler.EvaluateDueDeploymentsAsync(CancellationToken.None);

        _orchestrationMock.Verify(
            o => o.CreateRunAsync(
                It.Is<Deployment>(d => d.Id == deploymentId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EvaluateDue_DisabledDeployment_Skipped()
    {
        var (tenantId, essenceId) = await SeedTenantAndEssenceAsync("test-disabled");

        using (var db = CreateDb())
        {
            db.Deployments.Add(new Deployment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EssenceId = essenceId,
                Name = "Disabled",
                CronExpression = "* * * * *",
                IsEnabled = false
            });
            await db.SaveChangesAsync();
        }

        var scheduler = CreateScheduler();

        await scheduler.EvaluateDueDeploymentsAsync(CancellationToken.None);

        _orchestrationMock.Verify(
            o => o.CreateRunAsync(It.IsAny<Deployment>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateDue_NullCron_Skipped()
    {
        var (tenantId, essenceId) = await SeedTenantAndEssenceAsync("test-null");

        using (var db = CreateDb())
        {
            db.Deployments.Add(new Deployment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EssenceId = essenceId,
                Name = "NoCron",
                CronExpression = null,
                IsEnabled = true
            });
            await db.SaveChangesAsync();
        }

        var scheduler = CreateScheduler();

        await scheduler.EvaluateDueDeploymentsAsync(CancellationToken.None);

        _orchestrationMock.Verify(
            o => o.CreateRunAsync(It.IsAny<Deployment>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateDue_DuplicateWithinSameMinute_Skipped()
    {
        var (tenantId, essenceId) = await SeedTenantAndEssenceAsync("test-dedup");

        var deploymentId = Guid.NewGuid();
        var minuteFloor = DeploymentSchedulerService.FloorToMinute(DateTime.UtcNow);

        using (var db = CreateDb())
        {
            db.Deployments.Add(new Deployment
            {
                Id = deploymentId,
                TenantId = tenantId,
                EssenceId = essenceId,
                Name = "Dedup",
                CronExpression = "* * * * *",
                IsEnabled = true
            });
            await db.SaveChangesAsync();
        }

        // Add existing run in a separate save so CreatedAt override doesn't
        // overwrite our manually set value — use a direct insert approach
        using (var db = CreateDb())
        {
            var run = new DeploymentRun
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DeploymentId = deploymentId,
                Status = RunStatus.Queued,
                SnapshotJson = "{}"
            };
            db.DeploymentRuns.Add(run);
            await db.SaveChangesAsync();
            // CreatedAt is set to DateTime.UtcNow by SaveChanges, which falls
            // within the current minute — exactly what we need for dedup testing
        }

        var scheduler = CreateScheduler();

        await scheduler.EvaluateDueDeploymentsAsync(CancellationToken.None);

        _orchestrationMock.Verify(
            o => o.CreateRunAsync(It.IsAny<Deployment>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateDue_InvalidCron_LogsWarningAndSkips()
    {
        var (tenantId, essenceId) = await SeedTenantAndEssenceAsync("test-invalid");

        using (var db = CreateDb())
        {
            db.Deployments.Add(new Deployment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EssenceId = essenceId,
                Name = "BadCron",
                CronExpression = "not valid cron",
                IsEnabled = true
            });
            await db.SaveChangesAsync();
        }

        var scheduler = CreateScheduler();

        await scheduler.EvaluateDueDeploymentsAsync(CancellationToken.None);

        _orchestrationMock.Verify(
            o => o.CreateRunAsync(It.IsAny<Deployment>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void FloorToMinute_TruncatesSeconds()
    {
        var input = new DateTime(2026, 3, 12, 14, 30, 45, 123, DateTimeKind.Utc);
        var result = DeploymentSchedulerService.FloorToMinute(input);
        Assert.Equal(new DateTime(2026, 3, 12, 14, 30, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public async Task EvaluateDue_NonMatchingCron_Skipped()
    {
        var (tenantId, essenceId) = await SeedTenantAndEssenceAsync("test-nomatch");

        using (var db = CreateDb())
        {
            // Cron for Feb 30th — will never match
            db.Deployments.Add(new Deployment
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                EssenceId = essenceId,
                Name = "NeverMatch",
                CronExpression = "0 0 30 2 *",
                IsEnabled = true
            });
            await db.SaveChangesAsync();
        }

        var scheduler = CreateScheduler();

        await scheduler.EvaluateDueDeploymentsAsync(CancellationToken.None);

        _orchestrationMock.Verify(
            o => o.CreateRunAsync(It.IsAny<Deployment>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private DeploymentSchedulerService CreateScheduler()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = _serviceProvider.GetRequiredService<ILogger<DeploymentSchedulerService>>();
        return new DeploymentSchedulerService(scopeFactory, logger);
    }
}
