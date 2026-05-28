using System.Text.Json;
using Aura.Worker.Executors;
using Azure.ResourceManager.Network;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

/// <summary>
/// Creates (or updates) an Azure Virtual Network with one or more subnets.
///
/// Parameters:
///   vnetName            (required) string
///   resourceGroupName   (required) string — must already exist
///   location            (required) string — Azure region, e.g. "eastus"
///   addressPrefixes     (optional) string[] — CIDRs for the VNet. Defaults to ["10.0.0.0/16"].
///   subnets             (optional) [{ name, addressPrefix }] — defaults to one "default" subnet at 10.0.0.0/24.
/// </summary>
public class CreateVirtualNetworkHandler : IOperationHandler
{
    private readonly ILogger<CreateVirtualNetworkHandler> _logger;

    public CreateVirtualNetworkHandler(ILogger<CreateVirtualNetworkHandler> logger)
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
        if (!parameters.TryGetProperty("location", out var locationProp) || locationProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: location (string)");

        var vnetName = vnetNameProp.GetString()!;
        var resourceGroupName = rgNameProp.GetString()!;
        var location = locationProp.GetString()!;

        var addressPrefixes = new List<string>();
        if (parameters.TryGetProperty("addressPrefixes", out var apProp) && apProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in apProp.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) addressPrefixes.Add(el.GetString()!);
        }
        if (addressPrefixes.Count == 0) addressPrefixes.Add("10.0.0.0/16");

        var subnets = new List<(string Name, string Cidr)>();
        if (parameters.TryGetProperty("subnets", out var snProp) && snProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in snProp.EnumerateArray())
            {
                if (!s.TryGetProperty("name", out var n) || !s.TryGetProperty("addressPrefix", out var p))
                    return new LayerExecutionResult(false,
                        "Each subnet must have 'name' and 'addressPrefix' (e.g. {\"name\":\"web\",\"addressPrefix\":\"10.0.1.0/24\"})");
                subnets.Add((n.GetString()!, p.GetString()!));
            }
        }
        if (subnets.Count == 0) subnets.Add(("default", "10.0.0.0/24"));

        try
        {
            var client = AzureClientFactory.Create(envVars);
            var subscription = await client.GetDefaultSubscriptionAsync(ct);
            var rgResource = (await subscription.GetResourceGroupAsync(resourceGroupName, ct)).Value;

            var vnetData = new VirtualNetworkData
            {
                Location = new global::Azure.Core.AzureLocation(location),
            };
            foreach (var cidr in addressPrefixes)
                vnetData.AddressPrefixes.Add(cidr);
            foreach (var (name, cidr) in subnets)
                vnetData.Subnets.Add(new SubnetData { Name = name, AddressPrefix = cidr });

            _logger.LogInformation(
                "Creating VNet {VNet} in {ResourceGroup} ({Location}) with {SubnetCount} subnet(s)",
                vnetName, resourceGroupName, location, subnets.Count);

            var op = await rgResource.GetVirtualNetworks()
                .CreateOrUpdateAsync(global::Azure.WaitUntil.Completed, vnetName, vnetData, ct);
            var vnet = op.Value;

            var subnetSummary = string.Join(", ", subnets.Select(s => $"{s.Name}={s.Cidr}"));
            return new LayerExecutionResult(true,
                $"VNet '{vnet.Data.Name}' ready in '{resourceGroupName}' ({location}); " +
                $"addressPrefixes=[{string.Join(",", addressPrefixes)}]; subnets=[{subnetSummary}].");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create VNet {VNet}", vnetName);
            return new LayerExecutionResult(false, $"Failed to create VNet: {ex.Message}");
        }
    }
}
