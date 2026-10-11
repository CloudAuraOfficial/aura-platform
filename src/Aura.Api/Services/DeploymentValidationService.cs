using Aura.Core.Entities;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Services;

/// <summary>
/// The outcome of validating a deployment. Errors hold the reasons a run would be refused, safe to show the user.
/// </summary>
public sealed record DeploymentValidationResult(bool IsValid, string Message, IReadOnlyList<string> Errors);

public interface IDeploymentValidationService
{
    /// <summary>
    /// Checks the essence a run of this deployment would freeze, with the same parser and operation-type checks
    /// as run creation. Does not create a run.
    /// </summary>
    Task<DeploymentValidationResult> ValidateAsync(Deployment deployment, CancellationToken ct = default);
}

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

    public async Task<DeploymentValidationResult> ValidateAsync(Deployment deployment, CancellationToken ct = default)
    {
        // Same lookup as run creation, which freezes this essence into the run snapshot.
        var essence = await _db.Essences
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == deployment.EssenceId, ct);
        if (essence is null)
        {
            _logger.LogWarning(
                "Deployment {DeploymentId} failed validation: essence {EssenceId} not found",
                deployment.Id, deployment.EssenceId);
            return Invalid("Essence not found.");
        }

        var check = _essenceValidator.CheckRunnable(essence.EssenceJson);
        if (check.Error is not null)
        {
            _logger.LogWarning(
                "Deployment {DeploymentId} failed validation against essence {EssenceId}: {Reason}",
                deployment.Id, deployment.EssenceId, check.Error);
            return Invalid(check.Error);
        }

        _logger.LogInformation(
            "Deployment {DeploymentId} passed validation against essence {EssenceId}",
            deployment.Id, deployment.EssenceId);
        return new DeploymentValidationResult(true, "Deployment structure is valid.", []);
    }

    private static DeploymentValidationResult Invalid(string reason) =>
        new(false, "Deployment cannot run until its essence is fixed.", [reason]);
}
