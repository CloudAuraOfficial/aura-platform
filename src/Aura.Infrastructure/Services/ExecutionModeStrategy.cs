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
    private readonly Func<string, bool>? _hasInProcessHandler;

    public ExecutionModeStrategy(IConfiguration config, ILogger<ExecutionModeStrategy> logger,
        Func<string, bool>? hasInProcessHandler = null)
    {
        _configuredMode = config["EXECUTION_MODE"] ?? "InProcess";
        _logger = logger;
        _hasInProcessHandler = hasInProcessHandler;
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

    private ExecutionMode ResolveEmissionLoad(DeploymentRun run, DeploymentLayer layer)
    {
        // Script executors stay in-process (need local file access for script paths)
        if (layer.ExecutorType != ExecutorType.Operation)
            return ExecutionMode.InProcess;

        // If an in-process handler is registered for this operation type,
        // prefer in-process execution over EmissionLoad container
        if (_hasInProcessHandler != null && !string.IsNullOrEmpty(layer.OperationType)
            && _hasInProcessHandler(layer.OperationType))
        {
            _logger.LogInformation(
                "Operation {OperationType} has in-process handler, using InProcess instead of EmissionLoad",
                layer.OperationType);
            return ExecutionMode.InProcess;
        }

        return ExecutionMode.EmissionLoadContainer;
    }

    private ExecutionMode ResolveAuto(DeploymentRun run, DeploymentLayer layer)
    {
        if (layer.ExecutorType != ExecutorType.Operation)
            return ExecutionMode.InProcess;

        // If an in-process handler is registered, prefer it
        if (_hasInProcessHandler != null && !string.IsNullOrEmpty(layer.OperationType)
            && _hasInProcessHandler(layer.OperationType))
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
