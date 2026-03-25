using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.Compute;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class StopVMHandler : IOperationHandler
{
    private readonly ILogger<StopVMHandler> _logger;

    public StopVMHandler(ILogger<StopVMHandler> logger)
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

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;
            var vm = (await rgResource.GetVirtualMachineAsync(vmName, cancellationToken: ct)).Value;

            _logger.LogInformation(
                "Deallocating VM {VM} in {ResourceGroup}", vmName, resourceGroup);

            await vm.DeallocateAsync(global::Azure.WaitUntil.Completed, cancellationToken: ct);

            return new LayerExecutionResult(true,
                $"VM '{vmName}' deallocated (stopped and billing halted).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deallocate VM {VM}", vmName);
            return new LayerExecutionResult(false, $"Failed to deallocate VM: {ex.Message}");
        }
    }
}
