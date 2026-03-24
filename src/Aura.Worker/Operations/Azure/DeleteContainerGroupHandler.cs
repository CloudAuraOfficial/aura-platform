using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.ContainerInstance;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class DeleteContainerGroupHandler : IOperationHandler
{
    private readonly ILogger<DeleteContainerGroupHandler> _logger;

    public DeleteContainerGroupHandler(ILogger<DeleteContainerGroupHandler> logger)
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
                "Deleting container group {ContainerGroup} in {ResourceGroup}",
                containerGroupName, resourceGroup);

            await cg.DeleteAsync(global::Azure.WaitUntil.Completed, ct);

            return new LayerExecutionResult(true,
                $"Container group '{containerGroupName}' deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete container group {ContainerGroup}", containerGroupName);
            return new LayerExecutionResult(false, $"Failed to delete container group: {ex.Message}");
        }
    }
}
