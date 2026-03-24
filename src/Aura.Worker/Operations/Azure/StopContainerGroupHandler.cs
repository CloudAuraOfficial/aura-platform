using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.ContainerInstance;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class StopContainerGroupHandler : IOperationHandler
{
    private readonly ILogger<StopContainerGroupHandler> _logger;

    public StopContainerGroupHandler(ILogger<StopContainerGroupHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("containerGroupName", out var groupNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: containerGroupName");

        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        var containerGroupName = groupNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;
            var cg = (await rgResource.GetContainerGroupAsync(containerGroupName, ct)).Value;

            _logger.LogInformation(
                "Stopping container group {ContainerGroup} in {ResourceGroup}",
                containerGroupName, resourceGroup);

            await cg.StopAsync(ct);

            return new LayerExecutionResult(true,
                $"Container group '{containerGroupName}' stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop container group {ContainerGroup}", containerGroupName);
            return new LayerExecutionResult(false, $"Failed to stop container group: {ex.Message}");
        }
    }
}
