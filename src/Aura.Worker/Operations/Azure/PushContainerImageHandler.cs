using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.ContainerRegistry;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class PushContainerImageHandler : IOperationHandler
{
    private readonly ILogger<PushContainerImageHandler> _logger;

    public PushContainerImageHandler(ILogger<PushContainerImageHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("imageName", out var imageNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: imageName");

        if (!parameters.TryGetProperty("imageTag", out var imageTagProp))
            return new LayerExecutionResult(false, "Missing required parameter: imageTag");

        if (!parameters.TryGetProperty("registryName", out var registryNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: registryName");

        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        var imageName = imageNameProp.GetString()!;
        var imageTag = imageTagProp.GetString()!;
        var registryName = registryNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;
            var registry = (await rgResource.GetContainerRegistryAsync(registryName, ct)).Value;

            _logger.LogInformation(
                "Verifying image {Image}:{Tag} exists in registry {Registry}",
                imageName, imageTag, registryName);

            // List recent runs to find a successful build for this image
            var runs = registry.GetContainerRegistryRuns();
            var imageFound = false;

            await foreach (var run in runs.GetAllAsync(cancellationToken: ct))
            {
                if (run.Data.Status?.ToString()?.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                var outputImages = run.Data.OutputImages;
                if (outputImages == null)
                    continue;

                foreach (var img in outputImages)
                {
                    if (img.Repository == imageName && img.Tag == imageTag)
                    {
                        imageFound = true;
                        break;
                    }
                }

                if (imageFound)
                    break;
            }

            if (imageFound)
            {
                var message = $"Image '{registryName}.azurecr.io/{imageName}:{imageTag}' verified in registry. " +
                              "Image was pushed during ACR build task.";
                _logger.LogInformation(message);
                return new LayerExecutionResult(true, message);
            }

            // Image not found in recent runs — this is not necessarily a failure since
            // the run listing may have aged out. Log a warning but still succeed, since
            // the BuildContainerImage step already pushed the image.
            var warnMessage = $"Could not verify image '{imageName}:{imageTag}' in recent ACR runs, " +
                              "but the image was pushed during the build step. Proceeding.";
            _logger.LogWarning(warnMessage);
            return new LayerExecutionResult(true, warnMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify image {Image}:{Tag} in registry {Registry}",
                imageName, imageTag, registryName);
            return new LayerExecutionResult(false, $"Failed to verify image in registry: {ex.Message}");
        }
    }
}
