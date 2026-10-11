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
        // The same refusals run creation makes, in the same order.
        if (!deployment.IsEnabled)
            return Invalid(deployment, ["Deployment is disabled."]);

        // Reads only the essence JSON. The tenant query filter applies, so another tenant's essence is "not found".
        var essenceJson = await _db.Essences
            .AsNoTracking()
            .Where(e => e.Id == deployment.EssenceId)
            .Select(e => e.EssenceJson)
            .FirstOrDefaultAsync(ct);

        if (essenceJson is null)
            return Invalid(deployment, ["Essence not found."]);

        // Only the verdict is used here, so the canonical rewrite is skipped.
        var check = _essenceValidator.Validate(essenceJson, cloud: null, canonicalize: false);
        if (!check.IsValid)
            return Invalid(deployment, check.Errors);

        _logger.LogInformation(
            "Deployment {DeploymentId} validated: essence {EssenceId} is runnable",
            deployment.Id, deployment.EssenceId);
        return new DeploymentValidationResponse(true, "Deployment structure is valid.", []);
    }

    private DeploymentValidationResponse Invalid(Deployment deployment, IReadOnlyList<string> errors)
    {
        _logger.LogWarning(
            "Deployment {DeploymentId} failed validation: essence {EssenceId}, reasons={Reasons}",
            deployment.Id, deployment.EssenceId, string.Join(" | ", errors));
        return new DeploymentValidationResponse(false, "Deployment failed validation.", errors);
    }
}
