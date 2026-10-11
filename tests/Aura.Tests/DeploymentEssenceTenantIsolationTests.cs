using Aura.Api.Controllers;
using Aura.Api.Services;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Aura.Tests;

// Cross-tenant checks for deployments that reference an essence. Tenant B's essence must never be
// reachable from tenant A's requests.
public class DeploymentEssenceTenantIsolationTests
{
    private const string EssenceJson =
        """
        {
          "layers": {
            "create-rg": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": [] }
          }
        }
        """;

    private static AuraDbContext CreateDb(string dbName, Guid tenantId) =>
        new(new DbContextOptionsBuilder<AuraDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeTenant(tenantId));

    // Seeds an essence owned by tenantId, through that tenant's own context, and returns its id.
    private static async Task<Guid> SeedEssenceAsync(string dbName, Guid tenantId)
    {
        using var db = CreateDb(dbName, tenantId);
        var essence = new Essence
        {
            TenantId = tenantId,
            Name = "tenant-b-essence",
            CloudAccountId = Guid.NewGuid(),
            EssenceJson = EssenceJson
        };
        db.Essences.Add(essence);
        await db.SaveChangesAsync();
        return essence.Id;
    }

    [Fact]
    public async Task Create_refuses_an_essence_owned_by_another_tenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var foreignEssenceId = await SeedEssenceAsync(dbName, tenantB);

        using var db = CreateDb(dbName, tenantA);
        var controller = new DeploymentsController(
            db, new FakeTenant(tenantA), Mock.Of<IDeploymentOrchestrationService>(),
            Mock.Of<ICloudCostEstimatorFactory>(), Mock.Of<IDeploymentValidationService>());

        var result = await controller.Create(
            new CreateDeploymentRequest(foreignEssenceId, "stolen", null, null));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(bad.Value);
        Assert.Equal("Essence not found.", error.Message);
        // No deployment was stored for either tenant.
        Assert.Equal(0, await db.Deployments.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Validate_does_not_read_an_essence_owned_by_another_tenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var foreignEssenceId = await SeedEssenceAsync(dbName, tenantB);

        using var db = CreateDb(dbName, tenantA);
        // A tenant-A deployment that points at tenant B's essence. Create refuses to store one, so this
        // row is written directly to test that validate would not read the foreign essence even if it existed.
        var deployment = new Deployment { TenantId = tenantA, EssenceId = foreignEssenceId, Name = "d" };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        var service = new DeploymentValidationService(
            db, new EssenceValidator(), NullLogger<DeploymentValidationService>.Instance);

        var result = await service.ValidateAsync(deployment);

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "Essence not found." }, result.Errors);
        Assert.DoesNotContain(result.Errors, e => e.Contains("CreateResourceGroup"));
    }
}
