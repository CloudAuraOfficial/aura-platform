using Aura.Core.DTOs;
using Aura.Core.Entities;

namespace Aura.Api.Services;

public interface IDeploymentValidationService
{
    /// <summary>
    /// Checks the deployment's current essence with the same parser run creation uses and the operation-type
    /// checks save applies. Returns the real reasons, never a fixed verdict.
    /// </summary>
    Task<DeploymentValidationResponse> ValidateAsync(Deployment deployment, CancellationToken ct = default);
}
