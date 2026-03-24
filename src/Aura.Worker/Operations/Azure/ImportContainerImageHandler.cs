using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.ContainerRegistry;
using Azure.ResourceManager.ContainerRegistry.Models;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class ImportContainerImageHandler : IOperationHandler
{
    private readonly ILogger<ImportContainerImageHandler> _logger;

    public ImportContainerImageHandler(ILogger<ImportContainerImageHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("sourceImage", out var sourceImageProp))
            return new LayerExecutionResult(false, "Missing required parameter: sourceImage");

        if (!parameters.TryGetProperty("targetImage", out var targetImageProp))
            return new LayerExecutionResult(false, "Missing required parameter: targetImage");

        if (!parameters.TryGetProperty("registryName", out var registryNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: registryName");

        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        var sourceImage = sourceImageProp.GetString()!;
        var targetImage = targetImageProp.GetString()!;
        var registryName = registryNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;
            var registry = (await rgResource.GetContainerRegistryAsync(registryName, ct)).Value;

            var importSource = new ContainerRegistryImportSource(sourceImage);
            var importContent = new ContainerRegistryImportImageContent(importSource)
            {
                TargetTags = { targetImage }
            };

            _logger.LogInformation(
                "Importing image {Source} as {Target} into registry {Registry}",
                sourceImage, targetImage, registryName);

            await registry.ImportImageAsync(
                global::Azure.WaitUntil.Completed, importContent, ct);

            return new LayerExecutionResult(true,
                $"Image '{sourceImage}' imported as '{targetImage}' into registry '{registryName}'.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import container image into {Registry}", registryName);
            return new LayerExecutionResult(false, $"Failed to import container image: {ex.Message}");
        }
    }
}
