using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Api.Services;

public sealed class DeploymentValidationService : IDeploymentValidationService
{
    private readonly AuraDbContext _db;
    private readonly IEssenceValidator _essenceValidator;
    private readonly ILogger<DeploymentValidationService> _logger;

    public DeploymentValidationService(
        AuraDbContext db, IEssenceValidator essenceValidator, ILogger<DeploymentValidationService> logger)
    {
        _db = db;
        _essenceValidator = essenceValidator;
        _logger = logger;
    }

    public async Task<DeploymentValidationResponse> ValidateAsync(Deployment deployment, CancellationToken ct = default)
    {
        var essence = await _db.Essences
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == deployment.EssenceId, ct);

        if (essence is null)
            return Invalid(deployment, "Essence not found.");

        var check = _essenceValidator.CheckRunnable(essence.EssenceJson);
        if (check.Error is not null)
            return Invalid(deployment, check.Error);

        _logger.LogInformation(
            "Deployment {DeploymentId} validated: essence {EssenceId} is runnable",
            deployment.Id, essence.Id);
        return new DeploymentValidationResponse(true, "Deployment structure is valid.", []);
    }

    private DeploymentValidationResponse Invalid(Deployment deployment, string reason)
    {
        _logger.LogWarning(
            "Deployment {DeploymentId} failed validation: essence {EssenceId}, reason={Reason}",
            deployment.Id, deployment.EssenceId, reason);
        return new DeploymentValidationResponse(false, "Deployment failed validation.", [reason]);
    }
}
