using Aura.Api.Services;
using Aura.Core.Enums;
using Aura.Core.Entities;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Aura.Tests;

public class DeploymentValidationServiceTests
{
    private const string Runnable =
        """
        {
          "baseEssence": { "cloudProvider": "Azure", "defaultRegion": "eastus", "baseLoad": "EmissionLoadVM", "uniqueId": "t" },
          "layers": {
            "create-rg": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": { "resourceGroup": "rg" }, "dependsOn": [] }
          }
        }
        """;

    private const string Cyclic =
        """
        {
          "layers": {
            "a": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": ["b"] },
            "b": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": {}, "dependsOn": ["a"] }
          }
        }
        """;

    private const string UnknownOperation =
        """
        {
          "layers": {
            "x": { "isEnabled": true, "operationType": "Nope", "parameters": {}, "dependsOn": [] }
          }
        }
        """;

    private static AuraDbContext CreateDb(string dbName, Guid tenantId) =>
        new(new DbContextOptionsBuilder<AuraDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeTenant(tenantId));

    private static DeploymentValidationService CreateService(AuraDbContext db) =>
        new(db, new EssenceValidator(), NullLogger<DeploymentValidationService>.Instance);

    // Seeds an essence and a deployment for it; returns the deployment.
    private static async Task<Deployment> SeedDeployment(AuraDbContext db, Guid tenantId, string essenceJson)
    {
        var essence = new Essence
        {
            TenantId = tenantId,
            Name = "essence",
            CloudAccountId = Guid.NewGuid(),
            EssenceJson = essenceJson
        };
        db.Essences.Add(essence);

        var deployment = new Deployment
        {
            TenantId = tenantId,
            EssenceId = essence.Id,
            Name = "deployment"
        };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();
        return deployment;
    }

    [Fact]
    public async Task ValidateAsync_returns_valid_for_an_essence_run_creation_can_use()
    {
        var tenantId = Guid.NewGuid();
        using var db = CreateDb(Guid.NewGuid().ToString(), tenantId);
        var deployment = await SeedDeployment(db, tenantId, Runnable);

        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.True(result.IsValid);
        Assert.Equal("Deployment structure is valid.", result.Message);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_returns_the_parser_error_for_a_cyclic_essence()
    {
        var tenantId = Guid.NewGuid();
        using var db = CreateDb(Guid.NewGuid().ToString(), tenantId);
        var deployment = await SeedDeployment(db, tenantId, Cyclic);

        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.False(result.IsValid);
        Assert.Equal("Deployment failed validation.", result.Message);
        Assert.Equal(new[] { "Cycle detected in layer dependencies." }, result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_returns_the_operation_type_error_for_an_unknown_type()
    {
        var tenantId = Guid.NewGuid();
        using var db = CreateDb(Guid.NewGuid().ToString(), tenantId);
        var deployment = await SeedDeployment(db, tenantId, UnknownOperation);

        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("layer 'x' uses 'Nope'", error);
    }

    [Fact]
    public async Task ValidateAsync_reports_a_missing_essence_as_invalid()
    {
        var tenantId = Guid.NewGuid();
        using var db = CreateDb(Guid.NewGuid().ToString(), tenantId);
        var deployment = new Deployment { TenantId = tenantId, EssenceId = Guid.NewGuid(), Name = "orphan" };

        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "Essence not found." }, result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_does_not_read_an_essence_from_another_tenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var ownerTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();

        Deployment deployment;
        using (var seed = CreateDb(dbName, otherTenant))
            deployment = await SeedDeployment(seed, otherTenant, Runnable);

        using var db = CreateDb(dbName, ownerTenant);
        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "Essence not found." }, result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_refuses_a_disabled_deployment_before_checking_its_essence()
    {
        var tenantId = Guid.NewGuid();
        using var db = CreateDb(Guid.NewGuid().ToString(), tenantId);
        var deployment = await SeedDeployment(db, tenantId, Runnable);
        deployment.IsEnabled = false;
        await db.SaveChangesAsync();

        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "Deployment is disabled." }, result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_lets_an_unexpected_validator_exception_propagate_instead_of_reporting_invalid()
    {
        // A parser bug is not a verdict on the essence. It must reach the 500 handler, not a 200 "invalid".
        var tenantId = Guid.NewGuid();
        using var db = CreateDb(Guid.NewGuid().ToString(), tenantId);
        var deployment = await SeedDeployment(db, tenantId, Runnable);
        var validator = new Mock<IEssenceValidator>();
        validator
            .Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<CloudProvider?>(), It.IsAny<bool>()))
            .Throws(new NullReferenceException("parser bug"));
        var service = new DeploymentValidationService(db, validator.Object, NullLogger<DeploymentValidationService>.Instance);

        await Assert.ThrowsAsync<NullReferenceException>(() => service.ValidateAsync(deployment));
    }
}
