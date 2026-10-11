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

public class DeploymentValidationTests
{
    private const string AzureBase = "\"baseEssence\": { \"cloudProvider\": \"Azure\", \"defaultRegion\": \"eastus\", \"baseLoad\": \"EmissionLoadVM\", \"uniqueId\": \"t\" }";

    private const string RunnableLayers =
        "\"create-rg\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": { \"resourceGroup\": \"rg\" }, \"dependsOn\": [] }";

    private static string Essence(string layers) => $$"""
        {
          {{AzureBase}},
          "layers": { {{layers}} }
        }
        """;

    private static AuraDbContext CreateDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AuraDbContext(options);
    }

    private static DeploymentValidationService CreateService(AuraDbContext db) =>
        new(db, new EssenceValidator(), NullLogger<DeploymentValidationService>.Instance);

    // Seeds a tenant-scoped essence and a deployment that runs it, and returns the deployment.
    private static async Task<Deployment> SeedDeploymentAsync(AuraDbContext db, string essenceJson)
    {
        var tenantId = Guid.NewGuid();
        var essence = new Essence
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = "essence", CloudAccountId = Guid.NewGuid(),
            EssenceJson = essenceJson
        };
        var deployment = new Deployment
        {
            Id = Guid.NewGuid(), TenantId = tenantId, EssenceId = essence.Id, Name = "deployment", IsEnabled = true
        };
        db.Essences.Add(essence);
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();
        return deployment;
    }

    [Fact]
    public async Task ValidateAsync_passes_an_essence_run_creation_would_accept()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateDb(dbName);
        var deployment = await SeedDeploymentAsync(db, Essence(RunnableLayers));

        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal("Deployment structure is valid.", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_returns_the_operation_type_error_and_names_the_layer()
    {
        using var db = CreateDb(Guid.NewGuid().ToString());
        var layers = "\"bogus\": { \"isEnabled\": true, \"operationType\": \"NotARealOperation\", \"parameters\": {}, \"dependsOn\": [] }";
        var deployment = await SeedDeploymentAsync(db, Essence(layers));

        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("layer 'bogus' uses 'NotARealOperation'", error);
        Assert.Contains("Valid types:", error);
    }

    [Fact]
    public async Task ValidateAsync_returns_the_parser_error_run_creation_would_raise()
    {
        using var db = CreateDb(Guid.NewGuid().ToString());
        var cycle = "\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"b\"] }, " +
                    "\"b\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"a\"] }";
        var deployment = await SeedDeploymentAsync(db, Essence(cycle));

        var result = await CreateService(db).ValidateAsync(deployment);

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "Cycle detected in layer dependencies." }, result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_fails_when_the_essence_is_missing()
    {
        using var db = CreateDb(Guid.NewGuid().ToString());
        var orphan = new Deployment
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), EssenceId = Guid.NewGuid(), Name = "orphan", IsEnabled = true
        };

        var result = await CreateService(db).ValidateAsync(orphan);

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "Essence not found." }, result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_does_not_create_a_run()
    {
        using var db = CreateDb(Guid.NewGuid().ToString());
        var deployment = await SeedDeploymentAsync(db, Essence(RunnableLayers));

        await CreateService(db).ValidateAsync(deployment);

        Assert.Equal(0, await db.DeploymentRuns.CountAsync());
        Assert.Equal(0, await db.DeploymentLayers.CountAsync());
    }

    [Fact]
    public async Task Validate_endpoint_returns_the_real_errors_for_an_invalid_essence()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateDb(dbName);
        var layers = "\"bogus\": { \"isEnabled\": true, \"operationType\": \"NotARealOperation\", \"parameters\": {}, \"dependsOn\": [] }";
        var deployment = await SeedDeploymentAsync(db, Essence(layers));
        var controller = CreateController(db);

        var response = await controller.Validate(deployment.Id);

        var ok = Assert.IsType<OkObjectResult>(response);
        var result = Assert.IsType<DeploymentValidationResult>(ok.Value);
        Assert.False(result.IsValid);
        Assert.Contains("NotARealOperation", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task Validate_endpoint_returns_valid_for_a_runnable_essence()
    {
        using var db = CreateDb(Guid.NewGuid().ToString());
        var deployment = await SeedDeploymentAsync(db, Essence(RunnableLayers));

        var response = await CreateController(db).Validate(deployment.Id);

        var result = Assert.IsType<DeploymentValidationResult>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_endpoint_returns_not_found_for_an_unknown_deployment()
    {
        using var db = CreateDb(Guid.NewGuid().ToString());

        var response = await CreateController(db).Validate(Guid.NewGuid());

        var notFound = Assert.IsType<NotFoundObjectResult>(response);
        var error = Assert.IsType<ErrorResponse>(notFound.Value);
        Assert.Equal("not_found", error.Error);
        Assert.Equal(404, error.StatusCode);
    }

    private static DeploymentsController CreateController(AuraDbContext db) =>
        new(db,
            Mock.Of<ITenantContext>(t => t.TenantId == Guid.Empty),
            Mock.Of<IDeploymentOrchestrationService>(),
            Mock.Of<ICloudCostEstimatorFactory>(),
            CreateService(db));
}
