using Aura.Core.Entities;
using Aura.Core.Enums;

namespace Aura.Core.Interfaces;

public interface IExecutionModeStrategy
{
    ExecutionMode Resolve(DeploymentRun run, DeploymentLayer layer);
}
