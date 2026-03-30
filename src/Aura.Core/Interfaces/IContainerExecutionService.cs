using Aura.Core.Models;

namespace Aura.Core.Interfaces;

public interface IContainerExecutionService
{
    Task<ContainerExecutionResult> ExecuteAsync(
        ContainerExecutionRequest request,
        CancellationToken ct = default);
}
