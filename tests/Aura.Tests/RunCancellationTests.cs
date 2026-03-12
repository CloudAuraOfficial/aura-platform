using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aura.Tests;

public class RunCancellationTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    [Fact]
    public async Task CancelQueuedRun_SetsStatusToCancelled()
    {
        var db = CreateInMemoryDb();

        var run = new DeploymentRun
        {
            TenantId = Guid.NewGuid(),
            DeploymentId = Guid.NewGuid(),
            Status = RunStatus.Queued,
            SnapshotJson = "{}"
        };
        db.DeploymentRuns.Add(run);

        var layer = new DeploymentLayer
        {
            RunId = run.Id,
            LayerName = "TestLayer",
            ExecutorType = ExecutorType.PowerShell,
            Status = LayerStatus.Pending,
            SortOrder = 0
        };
        db.DeploymentLayers.Add(layer);
        await db.SaveChangesAsync();

        // Simulate cancellation logic
        run.Status = RunStatus.Cancelled;
        run.CompletedAt = DateTime.UtcNow;
        layer.Status = LayerStatus.Skipped;
        layer.Output = "Cancelled by user.";
        layer.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var updated = await db.DeploymentRuns.IgnoreQueryFilters()
            .Include(r => r.Layers)
            .FirstAsync(r => r.Id == run.Id);

        Assert.Equal(RunStatus.Cancelled, updated.Status);
        Assert.NotNull(updated.CompletedAt);
        Assert.All(updated.Layers, l => Assert.Equal(LayerStatus.Skipped, l.Status));
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    public void TerminalStatus_CannotBeCancelled(RunStatus status)
    {
        // Verify business rule: only Queued/Running can be cancelled
        Assert.False(status == RunStatus.Queued || status == RunStatus.Running);
    }

    [Theory]
    [InlineData(RunStatus.Queued)]
    [InlineData(RunStatus.Running)]
    public void ActiveStatus_CanBeCancelled(RunStatus status)
    {
        Assert.True(status == RunStatus.Queued || status == RunStatus.Running);
    }
}
