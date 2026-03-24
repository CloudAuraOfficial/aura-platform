using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class CreateResourceGroupHandler : IOperationHandler
{
    private readonly ILogger<CreateResourceGroupHandler> _logger;

    public CreateResourceGroupHandler(ILogger<CreateResourceGroupHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("resourceGroupName", out var rgNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroupName");

        if (!parameters.TryGetProperty("location", out var locationProp))
            return new LayerExecutionResult(false, "Missing required parameter: location");

        var resourceGroupName = rgNameProp.GetString()!;
        var location = locationProp.GetString()!;

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgCollection = subscription.GetResourceGroups();

            var rgData = new ResourceGroupData(new global::Azure.Core.AzureLocation(location));

            _logger.LogInformation(
                "Creating resource group {ResourceGroup} in {Location}",
                resourceGroupName, location);

            var operation = await rgCollection.CreateOrUpdateAsync(
                global::Azure.WaitUntil.Completed, resourceGroupName, rgData, ct);

            var rg = operation.Value;

            return new LayerExecutionResult(true,
                $"Resource group '{rg.Data.Name}' created/updated in '{rg.Data.Location}'.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create resource group {ResourceGroup}", resourceGroupName);
            return new LayerExecutionResult(false, $"Failed to create resource group: {ex.Message}");
        }
    }
}
