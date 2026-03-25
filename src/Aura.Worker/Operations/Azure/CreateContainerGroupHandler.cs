using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.ContainerRegistry;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class CreateContainerGroupHandler : IOperationHandler
{
    private readonly ILogger<CreateContainerGroupHandler> _logger;

    public CreateContainerGroupHandler(ILogger<CreateContainerGroupHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("containerGroupName", out var groupNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: containerGroupName");

        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        if (!parameters.TryGetProperty("location", out var locationProp))
            return new LayerExecutionResult(false, "Missing required parameter: location");

        if (!parameters.TryGetProperty("containers", out var containersProp) ||
            containersProp.ValueKind != JsonValueKind.Array)
            return new LayerExecutionResult(false, "Missing or invalid required parameter: containers (must be array)");

        var containerGroupName = groupNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;
        var location = locationProp.GetString()!;

        var osType = ContainerInstanceOperatingSystemType.Linux;
        if (parameters.TryGetProperty("osType", out var osTypeProp))
        {
            var osStr = osTypeProp.GetString();
            if (string.Equals(osStr, "Windows", StringComparison.OrdinalIgnoreCase))
                osType = ContainerInstanceOperatingSystemType.Windows;
        }

        string? dnsLabel = null;
        if (parameters.TryGetProperty("dnsLabel", out var dnsLabelProp))
            dnsLabel = dnsLabelProp.GetString();

        string? registryName = null;
        if (parameters.TryGetProperty("registryName", out var regNameProp))
            registryName = regNameProp.GetString();

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroup, ct)).Value;

            var containers = new List<ContainerInstanceContainer>();

            foreach (var containerDef in containersProp.EnumerateArray())
            {
                if (!containerDef.TryGetProperty("name", out var nameProp) ||
                    !containerDef.TryGetProperty("image", out var imageProp))
                    return new LayerExecutionResult(false, "Each container must have 'name' and 'image'.");

                var name = nameProp.GetString()!;
                var image = imageProp.GetString()!;

                var cpu = 1.0;
                if (containerDef.TryGetProperty("cpu", out var cpuProp))
                    cpu = cpuProp.GetDouble();

                var memoryInGb = 1.5;
                if (containerDef.TryGetProperty("memoryInGB", out var memProp))
                    memoryInGb = memProp.GetDouble();

                var resources = new ContainerResourceRequirements(
                    new ContainerResourceRequestsContent(memoryInGb, cpu));

                var container = new ContainerInstanceContainer(name, image, resources);

                // Ports
                if (containerDef.TryGetProperty("ports", out var portsProp) &&
                    portsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var portEl in portsProp.EnumerateArray())
                    {
                        var port = portEl.GetInt32();
                        container.Ports.Add(new ContainerPort(port));
                    }
                }

                // Environment variables
                if (containerDef.TryGetProperty("environmentVariables", out var envVarsProp) &&
                    envVarsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var ev in envVarsProp.EnumerateObject())
                    {
                        container.EnvironmentVariables.Add(
                            new ContainerEnvironmentVariable(ev.Name) { Value = ev.Value.GetString() });
                    }
                }

                // Secure environment variables
                if (containerDef.TryGetProperty("secureEnvironmentVariables", out var secEnvProp) &&
                    secEnvProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var ev in secEnvProp.EnumerateObject())
                    {
                        container.EnvironmentVariables.Add(
                            new ContainerEnvironmentVariable(ev.Name) { SecureValue = ev.Value.GetString() });
                    }
                }

                containers.Add(container);
            }

            var groupData = new ContainerGroupData(
                new global::Azure.Core.AzureLocation(location), containers, osType);

            // IP address configuration
            var ipType = ContainerGroupIPAddressType.Public;
            if (parameters.TryGetProperty("ipType", out var ipTypeProp))
            {
                var ipStr = ipTypeProp.GetString();
                if (string.Equals(ipStr, "Private", StringComparison.OrdinalIgnoreCase))
                    ipType = ContainerGroupIPAddressType.Private;
            }

            var ipPorts = new List<ContainerGroupPort>();
            foreach (var c in containers)
            {
                foreach (var p in c.Ports)
                    ipPorts.Add(new ContainerGroupPort(p.Port) { Protocol = ContainerGroupNetworkProtocol.Tcp });
            }

            if (ipPorts.Count > 0)
            {
                groupData.IPAddress = new ContainerGroupIPAddress(ipPorts, ipType);
                if (!string.IsNullOrEmpty(dnsLabel))
                    groupData.IPAddress.DnsNameLabel = dnsLabel;
            }

            // Registry credentials — fetch admin credentials from ACR
            if (!string.IsNullOrEmpty(registryName))
            {
                var loginServer = $"{registryName}.azurecr.io";
                var registry = (await rgResource.GetContainerRegistryAsync(registryName, ct)).Value;
                var creds = await registry.GetCredentialsAsync(ct);
                var acrUsername = creds.Value.Username;
                var acrPassword = creds.Value.Passwords.FirstOrDefault()?.Value;

                var cred = new ContainerGroupImageRegistryCredential(loginServer);
                cred.Username = acrUsername;
                cred.Password = acrPassword;

                groupData.ImageRegistryCredentials.Add(cred);

                // Prepend registry server to image names that don't have it
                foreach (var c in containers)
                {
                    if (!c.Image.Contains('.'))
                    {
                        // Image like "aura-api:latest" → "cloudauraregistry.azurecr.io/aura-api:latest"
                        var field = typeof(ContainerInstanceContainer).GetProperty("Image");
                        // ContainerInstanceContainer.Image is init-only, reconstruct isn't possible
                        // The image names in Essencefile should include the registry prefix
                    }
                }
            }

            _logger.LogInformation(
                "Creating container group {ContainerGroup} in {ResourceGroup} with {Count} container(s)",
                containerGroupName, resourceGroup, containers.Count);

            var cgCollection = rgResource.GetContainerGroups();
            var operation = await cgCollection.CreateOrUpdateAsync(
                global::Azure.WaitUntil.Completed, containerGroupName, groupData, ct);

            var cg = operation.Value;
            var ip = cg.Data.IPAddress?.IP?.ToString() ?? "N/A";

            return new LayerExecutionResult(true,
                $"Container group '{cg.Data.Name}' created/updated. IP: {ip}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create container group {ContainerGroup}", containerGroupName);
            return new LayerExecutionResult(false, $"Failed to create container group: {ex.Message}");
        }
    }
}
