using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.ContainerRegistry;
using Azure.ResourceManager.ContainerRegistry.Models;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class CreateContainerRegistryHandler : IOperationHandler
{
    private readonly ILogger<CreateContainerRegistryHandler> _logger;

    public CreateContainerRegistryHandler(ILogger<CreateContainerRegistryHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("registryName", out var registryNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: registryName");

        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        var registryName = registryNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;

        var sku = "Basic";
        if (parameters.TryGetProperty("sku", out var skuProp))
            sku = skuProp.GetString() ?? "Basic";

        var adminEnabled = true;
        if (parameters.TryGetProperty("adminEnabled", out var adminProp))
            adminEnabled = adminProp.GetBoolean();

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;
            var registryCollection = rgResource.GetContainerRegistries();

            var skuObj = new ContainerRegistrySku(sku switch
            {
                "Standard" => ContainerRegistrySkuName.Standard,
                "Premium" => ContainerRegistrySkuName.Premium,
                _ => ContainerRegistrySkuName.Basic
            });

            var registryData = new ContainerRegistryData(rgResource.Data.Location, skuObj)
            {
                IsAdminUserEnabled = adminEnabled
            };

            _logger.LogInformation(
                "Creating container registry {Registry} in resource group {ResourceGroup}",
                registryName, resourceGroup);

            var operation = await registryCollection.CreateOrUpdateAsync(
                global::Azure.WaitUntil.Completed, registryName, registryData, ct);

            var registry = operation.Value;

            return new LayerExecutionResult(true,
                $"Container registry '{registry.Data.Name}' created/updated. Login server: {registry.Data.LoginServer}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create container registry {Registry}", registryName);
            return new LayerExecutionResult(false, $"Failed to create container registry: {ex.Message}");
        }
    }
}
