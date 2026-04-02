using System.Text.Json;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class DeleteResourceGroupHandler : IOperationHandler
{
    private readonly ILogger<DeleteResourceGroupHandler> _logger;

    public DeleteResourceGroupHandler(ILogger<DeleteResourceGroupHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("resourceGroupName", out var rgNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroupName");

        var resourceGroupName = rgNameProp.GetString()!;

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);

            _logger.LogInformation("Deleting resource group {ResourceGroup}", resourceGroupName);

            var rg = (await subscription.GetResourceGroupAsync(resourceGroupName, ct)).Value;
            await rg.DeleteAsync(global::Azure.WaitUntil.Completed, cancellationToken: ct);

            return new LayerExecutionResult(true,
                $"Resource group '{resourceGroupName}' deleted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete resource group {ResourceGroup}", resourceGroupName);
            return new LayerExecutionResult(false, $"Failed to delete resource group: {ex.Message}");
        }
    }
}
