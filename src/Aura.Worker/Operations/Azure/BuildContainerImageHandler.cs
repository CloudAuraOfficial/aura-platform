using System.Text.Json;
using Aura.Worker.Executors;
using Azure;
using Azure.ResourceManager.ContainerRegistry;
using Azure.ResourceManager.ContainerRegistry.Models;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class BuildContainerImageHandler : IOperationHandler
{
    private readonly ILogger<BuildContainerImageHandler> _logger;

    public BuildContainerImageHandler(ILogger<BuildContainerImageHandler> logger)
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

        if (!parameters.TryGetProperty("sourceUrl", out var sourceUrlProp))
            return new LayerExecutionResult(false, "Missing required parameter: sourceUrl");

        var imageName = imageNameProp.GetString()!;
        var imageTag = imageTagProp.GetString()!;
        var registryName = registryNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;
        var sourceUrl = sourceUrlProp.GetString()!;

        var dockerfilePath = "Dockerfile";
        if (parameters.TryGetProperty("dockerfilePath", out var dockerfilePathProp))
            dockerfilePath = dockerfilePathProp.GetString() ?? "Dockerfile";

        string? buildTarget = null;
        if (parameters.TryGetProperty("buildTarget", out var buildTargetProp))
            buildTarget = buildTargetProp.GetString();

        var timeoutSeconds = 600;
        if (parameters.TryGetProperty("timeoutSeconds", out var timeoutProp))
            timeoutSeconds = timeoutProp.GetInt32();

        var fullImageTag = $"{imageName}:{imageTag}";

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;
            var registry = (await rgResource.GetContainerRegistryAsync(registryName, ct)).Value;

            var platform = new ContainerRegistryPlatformProperties(ContainerRegistryOS.Linux);

            var buildContent = new ContainerRegistryDockerBuildContent(dockerfilePath, platform)
            {
                SourceLocation = sourceUrl,
                IsPushEnabled = true,
                TimeoutInSeconds = timeoutSeconds
            };

            buildContent.ImageNames.Add(fullImageTag);

            if (!string.IsNullOrEmpty(buildTarget))
                buildContent.Target = buildTarget;

            _logger.LogInformation(
                "Scheduling ACR build for {Image} from {Source} (this may take several minutes)...",
                fullImageTag, sourceUrl);

            // Schedule the run — WaitUntil.Started so we get the run ID immediately
            var operation = await registry.ScheduleRunAsync(WaitUntil.Started, buildContent, ct);

            // Get the run ID from the operation response headers
            // The operation creates a run resource we need to poll
            var runCollection = registry.GetContainerRegistryRuns();

            // Find the most recent run (the one we just created)
            ContainerRegistryRunResource? buildRun = null;
            await foreach (var r in runCollection.GetAllAsync(cancellationToken: ct))
            {
                buildRun = r;
                break; // Most recent first
            }

            if (buildRun == null)
                return new LayerExecutionResult(false, "ACR build was scheduled but no run found");

            var runId = buildRun.Data.RunId ?? "unknown";
            _logger.LogInformation("ACR build run {RunId} created. Polling for completion...", runId);

            // Poll until terminal state
            var terminalStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Succeeded", "Failed", "Canceled", "Error", "Timeout" };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var maxPollTime = TimeSpan.FromSeconds(timeoutSeconds + 120);
            var runStatus = buildRun.Data.Status?.ToString() ?? "Unknown";

            while (!terminalStates.Contains(runStatus) && stopwatch.Elapsed < maxPollTime)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                buildRun = (await buildRun.GetAsync(ct)).Value;
                runStatus = buildRun.Data.Status?.ToString() ?? "Unknown";
                _logger.LogInformation("ACR build {RunId}: {Status} ({Elapsed}s elapsed)",
                    runId, runStatus, (int)stopwatch.Elapsed.TotalSeconds);
            }

            // Get build log
            var logUrl = string.Empty;
            try
            {
                var logResult = await buildRun.GetLogSasUrlAsync(ct);
                logUrl = logResult.Value.LogLink;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not retrieve build log URL for run {RunId}", runId);
            }

            var isSuccess = string.Equals(runStatus, "Succeeded", StringComparison.OrdinalIgnoreCase);

            var output = $"ACR build run '{runId}' completed with status: {runStatus} in {(int)stopwatch.Elapsed.TotalSeconds}s. " +
                         $"Image: {registryName}.azurecr.io/{fullImageTag}";

            if (!string.IsNullOrEmpty(logUrl))
                output += $"\nBuild log: {logUrl}";

            return new LayerExecutionResult(isSuccess, output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ACR build task failed for image {Image} in registry {Registry}",
                fullImageTag, registryName);
            return new LayerExecutionResult(false, $"ACR build task failed: {ex.Message}");
        }
    }
}
