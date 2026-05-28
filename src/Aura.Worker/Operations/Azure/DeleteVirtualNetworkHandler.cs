using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.Network;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

/// <summary>
/// Deletes an Azure Virtual Network. Idempotent: missing VNet is treated as success.
///
/// Parameters:
///   vnetName            (required) string
///   resourceGroupName   (required) string
/// </summary>
public class DeleteVirtualNetworkHandler : IOperationHandler
{
    private readonly ILogger<DeleteVirtualNetworkHandler> _logger;

    public DeleteVirtualNetworkHandler(ILogger<DeleteVirtualNetworkHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("vnetName", out var vnetNameProp) || vnetNameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: vnetName (string)");
        if (!parameters.TryGetProperty("resourceGroupName", out var rgNameProp) || rgNameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroupName (string)");

        var vnetName = vnetNameProp.GetString()!;
        var resourceGroupName = rgNameProp.GetString()!;

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroupName, ct)).Value;

            var vnetCollection = rgResource.GetVirtualNetworks();
            if (!await vnetCollection.ExistsAsync(vnetName, cancellationToken: ct))
            {
                _logger.LogInformation("VNet {VNet} already absent in {ResourceGroup}; skipping", vnetName, resourceGroupName);
                return new LayerExecutionResult(true, $"VNet '{vnetName}' was already absent.");
            }

            var vnet = (await vnetCollection.GetAsync(vnetName, cancellationToken: ct)).Value;
            _logger.LogInformation("Deleting VNet {VNet} from {ResourceGroup}", vnetName, resourceGroupName);
            await vnet.DeleteAsync(global::Azure.WaitUntil.Completed, ct);

            return new LayerExecutionResult(true, $"VNet '{vnetName}' deleted from '{resourceGroupName}'.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete VNet {VNet}", vnetName);
            return new LayerExecutionResult(false, $"Failed to delete VNet: {ex.Message}");
        }
    }
}
