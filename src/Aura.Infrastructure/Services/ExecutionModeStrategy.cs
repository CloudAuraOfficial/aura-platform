using System.Text.Json;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class ExecutionModeStrategy : IExecutionModeStrategy
{
    private readonly string _configuredMode;
    private readonly ILogger<ExecutionModeStrategy> _logger;

    public ExecutionModeStrategy(IConfiguration config, ILogger<ExecutionModeStrategy> logger)
    {
        _configuredMode = config["EXECUTION_MODE"] ?? "InProcess";
        _logger = logger;
    }

    public ExecutionMode Resolve(DeploymentRun run, DeploymentLayer layer)
    {
        // If the layer explicitly declares EmissionLoad executor, always use container
        if (layer.ExecutorType == ExecutorType.EmissionLoad)
            return ExecutionMode.EmissionLoadContainer;

        var mode = _configuredMode.ToLowerInvariant() switch
        {
            "emissionload" => ResolveEmissionLoad(run, layer),
            "auto" => ResolveAuto(run, layer),
            _ => ExecutionMode.InProcess // "inprocess" or any unknown value
        };

        _logger.LogDebug(
            "Execution mode for run {RunId} layer {LayerName}: {Mode} (config={Config})",
            run.Id, layer.LayerName, mode, _configuredMode);

        return mode;
    }

    private static ExecutionMode ResolveEmissionLoad(DeploymentRun run, DeploymentLayer layer)
    {
        // In EmissionLoad mode, all Operation-type layers go to containers.
        // Script executors (PowerShell, Python, CSharp) stay in-process for now
        // since they need local file access for script paths.
        return layer.ExecutorType == ExecutorType.Operation
            ? ExecutionMode.EmissionLoadContainer
            : ExecutionMode.InProcess;
    }

    private static ExecutionMode ResolveAuto(DeploymentRun run, DeploymentLayer layer)
    {
        // Auto mode: check if the snapshot has a baseLoad field,
        // and the layer is an Operation type
        if (layer.ExecutorType != ExecutorType.Operation)
            return ExecutionMode.InProcess;

        if (string.IsNullOrEmpty(run.SnapshotJson))
            return ExecutionMode.InProcess;

        try
        {
            using var doc = JsonDocument.Parse(run.SnapshotJson);
            if (doc.RootElement.TryGetProperty("baseEssence", out var baseEssence)
                && baseEssence.TryGetProperty("baseLoad", out var baseLoad)
                && !string.IsNullOrEmpty(baseLoad.GetString()))
            {
                return ExecutionMode.EmissionLoadContainer;
            }
        }
        catch
        {
            // Invalid JSON — fall back to in-process
        }

        return ExecutionMode.InProcess;
    }
}
