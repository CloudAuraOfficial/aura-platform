using System.Text.Json;
using Aura.Worker.Executors;
using Google.Cloud.Compute.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// Creates a custom-mode VPC network plus one subnet in the requested region.
/// Network names + descriptions carry the aura:layer marker so DeleteNetwork
/// (the next handler) can find what we manage. Description format follows
/// the AWS tag pattern.
///
/// Parameters:
///   networkName   (required)
///   subnetName    (optional, default "<networkName>-subnet")
///   subnetCidr    (optional, default "10.0.0.0/24")
///   region        (optional, falls back to envVars["AURA_DEFAULT_REGION"], then "us-central1")
/// </summary>
public class CreateNetworkHandler : IOperationHandler
{
    private readonly ILogger<CreateNetworkHandler> _logger;
    public CreateNetworkHandler(ILogger<CreateNetworkHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("networkName", out var nProp) || nProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: networkName");

        var networkName = nProp.GetString()!;
        var subnetName = parameters.TryGetProperty("subnetName", out var sn) ? sn.GetString()! : $"{networkName}-subnet";
        var subnetCidr = parameters.TryGetProperty("subnetCidr", out var sc) ? sc.GetString()! : "10.0.0.0/24";
        var region = parameters.TryGetProperty("region", out var rg)
            ? rg.GetString()!
            : envVars.GetValueOrDefault("AURA_DEFAULT_REGION", "us-central1");
        var projectId = GcpClientFactory.ResolveProjectId(envVars);

        try
        {
            var networksClient = GcpClientFactory.CreateNetworks(envVars);
            var subnetsClient = GcpClientFactory.CreateSubnetworks(envVars);

            _logger.LogInformation("Creating network {NetworkName} in project {ProjectId}", networkName, projectId);
            var netOp = await networksClient.InsertAsync(projectId, new Network
            {
                Name = networkName,
                AutoCreateSubnetworks = false,
                Description = AuraDescription(layerName),
            }, ct);
            await netOp.PollUntilCompletedAsync();

            _logger.LogInformation("Creating subnet {SubnetName} ({Cidr}) in {Region}", subnetName, subnetCidr, region);
            var subnetOp = await subnetsClient.InsertAsync(projectId, region, new Subnetwork
            {
                Name = subnetName,
                Network = $"projects/{projectId}/global/networks/{networkName}",
                IpCidrRange = subnetCidr,
                Description = AuraDescription(layerName),
            }, ct);
            await subnetOp.PollUntilCompletedAsync();

            return new LayerExecutionResult(true,
                $"Network '{networkName}' created with subnet '{subnetName}' ({subnetCidr}) in {region}.");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to create network {NetworkName}", networkName);
            return new LayerExecutionResult(false, $"Failed to create network: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }

    internal static string AuraDescription(string layerName) =>
        $"aura:layer={layerName}; aura:managed=true";
}
