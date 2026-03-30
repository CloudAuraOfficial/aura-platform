using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Core.Models;
using Aura.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Executors;

public class EmissionLoadExecutor : ILayerExecutor
{
    private readonly IContainerExecutionService _containerService;
    private readonly EmissionLoadResolver _resolver;
    private readonly ILogger<EmissionLoadExecutor> _logger;

    public EmissionLoadExecutor(
        IContainerExecutionService containerService,
        EmissionLoadResolver resolver,
        ILogger<EmissionLoadExecutor> logger)
    {
        _containerService = containerService;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        DeploymentLayer layer, string workDir,
        Dictionary<string, string> envVars, CancellationToken ct)
    {
        // Resolve the EmissionLoad image from the run's snapshot
        var run = layer.Run;
        if (run is null)
        {
            _logger.LogError("Layer {LayerName} has no associated run", layer.LayerName);
            return new LayerExecutionResult(false, "No associated run found for EmissionLoad execution.");
        }

        EmissionLoadConfig config;
        try
        {
            config = await _resolver.ResolveFromSnapshotAsync(run.TenantId, run.SnapshotJson, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve EmissionLoad config for run {RunId}", run.Id);
            return new LayerExecutionResult(false, $"EmissionLoad resolution failed: {ex.Message}");
        }

        var fullImageName = $"{config.ImageName}:{config.ImageTag}";

        _logger.LogInformation(
            "Dispatching layer {LayerName} to EmissionLoad container {Image} for run {RunId}",
            layer.LayerName, fullImageName, run.Id);

        // Record the image used for audit
        layer.EmissionLoadImage = fullImageName;

        var request = new ContainerExecutionRequest(
            RunId: run.Id,
            LayerId: layer.Id,
            ImageName: fullImageName,
            EssenceJson: run.SnapshotJson,
            LayerName: layer.LayerName,
            OperationType: layer.OperationType,
            Parameters: layer.Parameters,
            EnvVars: envVars);

        var result = await _containerService.ExecuteAsync(request, ct);

        return new LayerExecutionResult(result.Success, result.Output);
    }
}
