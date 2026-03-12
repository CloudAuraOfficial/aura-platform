using Aura.Core.Entities;

namespace Aura.Core.Interfaces;

public interface IDeploymentOrchestrationService
{
    Task<DeploymentRun> CreateRunAsync(Deployment deployment, CancellationToken ct = default);
}
