using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class DeployArmTemplateHandler : IOperationHandler
{
    private readonly ILogger<DeployArmTemplateHandler> _logger;

    public DeployArmTemplateHandler(ILogger<DeployArmTemplateHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        if (!parameters.TryGetProperty("templatePath", out var templatePathProp))
            return new LayerExecutionResult(false, "Missing required parameter: templatePath");

        var resourceGroup = rgProp.GetString()!;
        var templatePath = templatePathProp.GetString()!;

        var deploymentName = "aura-arm-deployment";
        if (parameters.TryGetProperty("deploymentName", out var depNameProp))
            deploymentName = depNameProp.GetString() ?? deploymentName;

        // Read the ARM template from disk
        if (!File.Exists(templatePath))
            return new LayerExecutionResult(false, $"ARM template not found: {templatePath}");

        var templateJson = await File.ReadAllTextAsync(templatePath, ct);

        // Build ARM template parameters from the layer parameters
        var armParameters = new Dictionary<string, object>();
        if (parameters.TryGetProperty("templateParameters", out var templateParams))
        {
            foreach (var prop in templateParams.EnumerateObject())
            {
                armParameters[prop.Name] = new { value = GetParameterValue(prop.Value) };
            }
        }

        var armParametersJson = JsonSerializer.Serialize(new { parameters = armParameters });

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;
            var deployments = rgResource.GetArmDeployments();

            _logger.LogInformation(
                "Deploying ARM template to resource group {ResourceGroup} as {DeploymentName}",
                resourceGroup, deploymentName);

            var deploymentContent = new ArmDeploymentContent(
                new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
                {
                    Template = BinaryData.FromString(templateJson),
                    Parameters = BinaryData.FromString(armParametersJson),
                });

            var operation = await deployments.CreateOrUpdateAsync(
                global::Azure.WaitUntil.Completed, deploymentName, deploymentContent, ct);

            var deployment = operation.Value;
            var state = deployment.Data.Properties.ProvisioningState?.ToString() ?? "Unknown";

            // Extract outputs
            var outputSummary = "";
            if (deployment.Data.Properties.Outputs is not null)
            {
                try
                {
                    var outputs = JsonSerializer.Deserialize<JsonElement>(
                        deployment.Data.Properties.Outputs.ToString());
                    var outputParts = new List<string>();
                    foreach (var prop in outputs.EnumerateObject())
                    {
                        if (prop.Value.TryGetProperty("value", out var val))
                            outputParts.Add($"{prop.Name}={val}");
                    }
                    if (outputParts.Count > 0)
                        outputSummary = $"\nOutputs: {string.Join(", ", outputParts)}";
                }
                catch { /* best effort output parsing */ }
            }

            var success = state == "Succeeded";
            return new LayerExecutionResult(success,
                $"ARM deployment '{deploymentName}' completed with state: {state}.{outputSummary}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ARM template deployment failed for {DeploymentName}", deploymentName);
            return new LayerExecutionResult(false, $"ARM deployment failed: {ex.Message}");
        }
    }

    private static object GetParameterValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => element.GetString() ?? element.ToString()
    };
}
