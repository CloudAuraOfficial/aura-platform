using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class AuditServiceTests
{
    private static AuraDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    [Fact]
    public async Task LogAsync_CreatesAuditEntry()
    {
        var db = CreateInMemoryDb();
        var logger = Mock.Of<ILogger<AuditService>>();
        var service = new AuditService(db, logger);

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await service.LogAsync(tenantId, userId, "create", "Essence", Guid.NewGuid(), "test detail");

        var entries = await db.AuditLog.ToListAsync();
        Assert.Single(entries);
        Assert.Equal("create", entries[0].Action);
        Assert.Equal("Essence", entries[0].EntityType);
        Assert.Equal("test detail", entries[0].Detail);
        Assert.Equal(tenantId, entries[0].TenantId);
        Assert.Equal(userId, entries[0].UserId);
    }

    [Fact]
    public async Task LogAsync_MultipleCalls_CreatesMultipleEntries()
    {
        var db = CreateInMemoryDb();
        var logger = Mock.Of<ILogger<AuditService>>();
        var service = new AuditService(db, logger);

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await service.LogAsync(tenantId, userId, "create", "User");
        await service.LogAsync(tenantId, userId, "update", "User");
        await service.LogAsync(tenantId, userId, "delete", "User");

        Assert.Equal(3, await db.AuditLog.CountAsync());
    }
}
