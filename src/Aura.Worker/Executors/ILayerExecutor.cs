using Aura.Core.Entities;

namespace Aura.Worker.Executors;

public interface ILayerExecutor
{
    Task<LayerExecutionResult> ExecuteAsync(
        DeploymentLayer layer,
        string workDir,
        Dictionary<string, string> envVars,
        CancellationToken ct = default);
}

public sealed record LayerExecutionResult(bool Success, string Output);
