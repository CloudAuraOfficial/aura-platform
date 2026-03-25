using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class DeleteVMHandler : IOperationHandler
{
    private readonly ILogger<DeleteVMHandler> _logger;

    public DeleteVMHandler(ILogger<DeleteVMHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("vmName", out var vmNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: vmName");

        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        var vmName = vmNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;

        var deleteNetworking = true;
        if (parameters.TryGetProperty("deleteNetworking", out var delNetProp))
            deleteNetworking = delNetProp.GetBoolean();

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;

            // Delete VM
            _logger.LogInformation("Deleting VM {VM} in {ResourceGroup}", vmName, resourceGroup);
            var vm = (await rgResource.GetVirtualMachineAsync(vmName, cancellationToken: ct)).Value;
            await vm.DeleteAsync(global::Azure.WaitUntil.Completed, cancellationToken: ct);

            if (deleteNetworking)
            {
                // Delete NIC
                _logger.LogInformation("Deleting NIC {NIC}", $"{vmName}-nic");
                try
                {
                    var nic = (await rgResource.GetNetworkInterfaceAsync($"{vmName}-nic", cancellationToken: ct)).Value;
                    await nic.DeleteAsync(global::Azure.WaitUntil.Completed, ct);
                }
                catch (global::Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("NIC {NIC} not found, skipping", $"{vmName}-nic");
                }

                // Delete Public IP
                _logger.LogInformation("Deleting Public IP {IP}", $"{vmName}-ip");
                try
                {
                    var publicIp = (await rgResource.GetPublicIPAddressAsync($"{vmName}-ip", cancellationToken: ct)).Value;
                    await publicIp.DeleteAsync(global::Azure.WaitUntil.Completed, ct);
                }
                catch (global::Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("Public IP {IP} not found, skipping", $"{vmName}-ip");
                }

                // Delete NSG
                _logger.LogInformation("Deleting NSG {NSG}", $"{vmName}-nsg");
                try
                {
                    var nsg = (await rgResource.GetNetworkSecurityGroupAsync($"{vmName}-nsg", cancellationToken: ct)).Value;
                    await nsg.DeleteAsync(global::Azure.WaitUntil.Completed, ct);
                }
                catch (global::Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("NSG {NSG} not found, skipping", $"{vmName}-nsg");
                }

                // Delete VNet
                _logger.LogInformation("Deleting VNet {VNet}", $"{vmName}-vnet");
                try
                {
                    var vnet = (await rgResource.GetVirtualNetworkAsync($"{vmName}-vnet", cancellationToken: ct)).Value;
                    await vnet.DeleteAsync(global::Azure.WaitUntil.Completed, ct);
                }
                catch (global::Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("VNet {VNet} not found, skipping", $"{vmName}-vnet");
                }
            }

            var result = deleteNetworking
                ? $"VM '{vmName}' and associated networking resources deleted."
                : $"VM '{vmName}' deleted (networking resources retained).";

            return new LayerExecutionResult(true, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete VM {VM}", vmName);
            return new LayerExecutionResult(false, $"Failed to delete VM: {ex.Message}");
        }
    }
}
