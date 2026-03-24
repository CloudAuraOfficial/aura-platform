using System.Text.Json;
using Aura.Core.Entities;
using Aura.Worker.Operations;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Executors;

public class OperationExecutor : ILayerExecutor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OperationRegistry _registry;
    private readonly ILogger<OperationExecutor> _logger;

    public OperationExecutor(
        IServiceProvider serviceProvider,
        OperationRegistry registry,
        ILogger<OperationExecutor> logger)
    {
        _serviceProvider = serviceProvider;
        _registry = registry;
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        DeploymentLayer layer, string workDir, Dictionary<string, string> envVars, CancellationToken ct)
    {
        string? operationType = layer.OperationType;

        // Fallback: try to extract operationType from parameters JSON
        if (string.IsNullOrEmpty(operationType))
        {
            try
            {
                using var doc = JsonDocument.Parse(layer.Parameters);
                if (doc.RootElement.TryGetProperty("operationType", out var opProp))
                    operationType = opProp.GetString();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse parameters JSON for layer {LayerName}", layer.LayerName);
                return new LayerExecutionResult(false, $"Invalid parameters JSON: {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(operationType))
        {
            _logger.LogError("No operationType specified for layer {LayerName}", layer.LayerName);
            return new LayerExecutionResult(false, "No operationType specified in layer parameters.");
        }

        _logger.LogInformation("Executing operation {OperationType} for layer {LayerName}",
            operationType, layer.LayerName);

        try
        {
            var handler = _registry.Resolve(_serviceProvider, operationType);

            JsonElement parameters;
            using var doc = JsonDocument.Parse(layer.Parameters);
            parameters = doc.RootElement.Clone();

            return await handler.ExecuteAsync(layer.LayerName, parameters, envVars, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to resolve handler for operation {OperationType}", operationType);
            return new LayerExecutionResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operation {OperationType} failed for layer {LayerName}",
                operationType, layer.LayerName);
            return new LayerExecutionResult(false, $"Operation failed: {ex.Message}");
        }
    }
}
