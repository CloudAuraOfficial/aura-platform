using Aura.Api.Controllers;
using Aura.Api.Services;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Aura.Tests;

public class DeploymentValidateEndpointTests
{
    private sealed record FakeTenant(Guid TenantId) : ITenantContext;

    private static (DeploymentsController controller, AuraDbContext db) CreateController(
        Guid tenantId, IDeploymentValidationService validation)
    {
        var db = new AuraDbContext(
            new DbContextOptionsBuilder<AuraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new FakeTenant(tenantId));
        var controller = new DeploymentsController(
            db,
            new FakeTenant(tenantId),
            Mock.Of<IDeploymentOrchestrationService>(),
            Mock.Of<ICloudCostEstimatorFactory>(),
            validation);
        return (controller, db);
    }

    [Fact]
    public async Task Validate_returns_the_standard_not_found_error_for_an_unknown_deployment()
    {
        var (controller, db) = CreateController(Guid.NewGuid(), Mock.Of<IDeploymentValidationService>());
        using var _ = db;

        var result = await controller.Validate(Guid.NewGuid(), CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(notFound.Value);
        Assert.Equal("not_found", error.Error);
        Assert.Equal("Deployment not found.", error.Message);
        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task Validate_returns_the_service_verdict_with_its_errors_for_an_existing_deployment()
    {
        var tenantId = Guid.NewGuid();
        var validation = new Mock<IDeploymentValidationService>();
        validation
            .Setup(v => v.ValidateAsync(It.IsAny<Deployment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentValidationResponse(
                false, "Deployment failed validation.", new[] { "Cycle detected in layer dependencies." }));

        var (controller, db) = CreateController(tenantId, validation.Object);
        using var _ = db;
        var deployment = new Deployment { TenantId = tenantId, EssenceId = Guid.NewGuid(), Name = "d" };
        db.Deployments.Add(deployment);
        await db.SaveChangesAsync();

        var result = await controller.Validate(deployment.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<DeploymentValidationResponse>(ok.Value);
        Assert.False(body.IsValid);
        Assert.Equal(new[] { "Cycle detected in layer dependencies." }, body.Errors);
        validation.Verify(v => v.ValidateAsync(
            It.Is<Deployment>(d => d.Id == deployment.Id), It.IsAny<CancellationToken>()), Times.Once);
    }
}
