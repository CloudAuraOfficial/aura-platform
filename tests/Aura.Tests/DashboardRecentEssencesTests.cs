using Aura.Api.Controllers;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aura.Tests;

public class DashboardRecentEssencesTests
{
    [Fact]
    public async Task Provider_is_the_declared_one_or_null_never_a_placeholder()
    {
        var tenantId = Guid.NewGuid();
        await using var db = new AuraDbContext(
            new DbContextOptionsBuilder<AuraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new FakeTenant(tenantId));
        db.Essences.AddRange(
            new Essence { TenantId = tenantId, Name = "declared", UpdatedAt = DateTime.UtcNow,
                EssenceJson = """{"baseEssence":{"cloudProvider":"Azure"},"layers":{"a":{},"b":{}}}""" },
            new Essence { TenantId = tenantId, Name = "undeclared", UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
                EssenceJson = """{"layers":{"x":{"cloudAccountId":"c1"}}}""" });
        await db.SaveChangesAsync();

        var ok = Assert.IsType<OkObjectResult>(await new DashboardController(db).GetRecentEssences());
        var rows = Assert.IsAssignableFrom<IEnumerable<RecentEssenceResponse>>(ok.Value).ToDictionary(r => r.Name);

        Assert.Equal("Azure", rows["declared"].Provider);
        Assert.Equal(2, rows["declared"].LayerCount);
        Assert.Null(rows["undeclared"].Provider);
        Assert.Equal(1, rows["undeclared"].LayerCount);
    }
}
