using System.Text.Json;
using Aura.Worker.Executors;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// GCE lifecycle handlers (Start, Stop, Delete). All three take instanceName
/// + zone — GCE instance names aren't unique across zones the way AWS EC2
/// names are tag-scoped, so the zone has to be supplied. Same pattern as
/// the AWS lifecycle handlers from Epic 1.
/// </summary>
internal static class GceParams
{
    public static (string instanceName, string zone)? Read(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("instanceName", out var n) || n.ValueKind != JsonValueKind.String) return null;
        if (!parameters.TryGetProperty("zone", out var z) || z.ValueKind != JsonValueKind.String) return null;
        return (n.GetString()!, z.GetString()!);
    }
}

public class StartGceInstanceHandler : IOperationHandler
{
    private readonly ILogger<StartGceInstanceHandler> _logger;
    public StartGceInstanceHandler(ILogger<StartGceInstanceHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct = default)
    {
        var p = GceParams.Read(parameters);
        if (p is null) return new LayerExecutionResult(false, "Missing required parameters: instanceName, zone");
        var projectId = GcpClientFactory.ResolveProjectId(envVars);

        try
        {
            var client = GcpClientFactory.CreateInstances(envVars);
            _logger.LogInformation("Starting GCE {InstanceName} in {Zone}", p.Value.instanceName, p.Value.zone);
            var op = await client.StartAsync(projectId, p.Value.zone, p.Value.instanceName, ct);
            await op.PollUntilCompletedAsync();
            return new LayerExecutionResult(true, $"GCE '{p.Value.instanceName}' started.");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to start GCE {InstanceName}", p.Value.instanceName);
            return new LayerExecutionResult(false, $"Failed to start GCE: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }
}

public class StopGceInstanceHandler : IOperationHandler
{
    private readonly ILogger<StopGceInstanceHandler> _logger;
    public StopGceInstanceHandler(ILogger<StopGceInstanceHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct = default)
    {
        var p = GceParams.Read(parameters);
        if (p is null) return new LayerExecutionResult(false, "Missing required parameters: instanceName, zone");
        var projectId = GcpClientFactory.ResolveProjectId(envVars);

        try
        {
            var client = GcpClientFactory.CreateInstances(envVars);
            _logger.LogInformation("Stopping GCE {InstanceName} in {Zone}", p.Value.instanceName, p.Value.zone);
            var op = await client.StopAsync(projectId, p.Value.zone, p.Value.instanceName, ct);
            await op.PollUntilCompletedAsync();
            return new LayerExecutionResult(true, $"GCE '{p.Value.instanceName}' stopped.");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to stop GCE {InstanceName}", p.Value.instanceName);
            return new LayerExecutionResult(false, $"Failed to stop GCE: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }
}

public class DeleteGceInstanceHandler : IOperationHandler
{
    private readonly ILogger<DeleteGceInstanceHandler> _logger;
    public DeleteGceInstanceHandler(ILogger<DeleteGceInstanceHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct = default)
    {
        var p = GceParams.Read(parameters);
        if (p is null) return new LayerExecutionResult(false, "Missing required parameters: instanceName, zone");
        var projectId = GcpClientFactory.ResolveProjectId(envVars);

        try
        {
            var client = GcpClientFactory.CreateInstances(envVars);
            _logger.LogInformation("Deleting GCE {InstanceName} in {Zone}", p.Value.instanceName, p.Value.zone);
            var op = await client.DeleteAsync(projectId, p.Value.zone, p.Value.instanceName, ct);
            await op.PollUntilCompletedAsync();
            return new LayerExecutionResult(true, $"GCE '{p.Value.instanceName}' deleted.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return new LayerExecutionResult(true, $"GCE '{p.Value.instanceName}' not found — nothing to delete.");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to delete GCE {InstanceName}", p.Value.instanceName);
            return new LayerExecutionResult(false, $"Failed to delete GCE: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }
}
