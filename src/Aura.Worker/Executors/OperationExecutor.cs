using System.Diagnostics;
using System.Text.Json;
using Aura.Core.Entities;
using Aura.Worker.Operations;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Executors;

public class OperationExecutor : ILayerExecutor
{
    private static readonly ActivitySource Source = new("Aura.Worker.Operations");

    private readonly IServiceProvider _serviceProvider;
    private readonly OperationRegistry _registry;
    private readonly ILogger<OperationExecutor> _logger;
    private readonly TimeSpan _operationTimeout;

    public OperationExecutor(
        IServiceProvider serviceProvider,
        OperationRegistry registry,
        ILogger<OperationExecutor> logger,
        TimeSpan? operationTimeout = null)
    {
        _serviceProvider = serviceProvider;
        _registry = registry;
        _logger = logger;
        _operationTimeout = operationTimeout ?? ResolveTimeoutFromEnv();
    }

    // Client-side ceiling on a single cloud operation (#16): the Azure/AWS/GCP SDK
    // long-running-operation calls (WaitUntil.Completed) can hang indefinitely — a
    // VNet creation once sat 19+ min — pinning a worker slot with no upper bound.
    // Env-tunable; the default is deliberately generous so genuinely slow provisioning
    // (ARM/AKS) isn't preempted — the point is to bound "forever", not to be aggressive.
    private static TimeSpan ResolveTimeoutFromEnv()
    {
        var raw = Environment.GetEnvironmentVariable("AURA_OPERATION_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromMinutes(30);
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
            using var activity = Source.StartActivity($"Operation:{operationType}");
            activity?.SetTag("operation.type", operationType);
            activity?.SetTag("operation.layer", layer.LayerName);

            var handler = _registry.Resolve(_serviceProvider, operationType);

            // Resolve ${BYOS_*} references in parameters using credentials from envVars
            var resolvedParams = ByosResolver.Resolve(layer.Parameters, envVars);

            JsonElement parameters;
            using var doc = JsonDocument.Parse(resolvedParams);
            parameters = doc.RootElement.Clone();

            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opCts.CancelAfter(_operationTimeout);

            LayerExecutionResult result;
            try
            {
                result = await handler.ExecuteAsync(layer.LayerName, parameters, envVars, opCts.Token);
            }
            catch (OperationCanceledException) when (opCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Our timeout fired (not a caller/shutdown cancellation) → fail fast with a clear message.
                _logger.LogError("Operation {OperationType} for layer {LayerName} exceeded the {Minutes:0}-min client-side timeout",
                    operationType, layer.LayerName, _operationTimeout.TotalMinutes);
                activity?.SetStatus(ActivityStatusCode.Error, "operation timeout");
                return new LayerExecutionResult(false,
                    $"Operation '{operationType}' timed out after {_operationTimeout.TotalMinutes:0} min " +
                    "(client-side ceiling; set AURA_OPERATION_TIMEOUT_SECONDS to adjust).");
            }

            if (!result.Success)
                activity?.SetStatus(ActivityStatusCode.Error, result.Output?[..Math.Min(200, result.Output?.Length ?? 0)]);
            return result;
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
