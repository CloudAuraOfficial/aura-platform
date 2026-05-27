using System.Text.Json;
using Aura.Worker.Executors;
using Google.Cloud.Compute.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// Tears down a network previously created by CreateNetworkHandler. GCP
/// requires children to be removed first: firewall rules attached to the
/// network, then subnets in every region, then the network itself.
///
/// Parameters:
///   networkName  (required)
/// </summary>
public class DeleteNetworkHandler : IOperationHandler
{
    private readonly ILogger<DeleteNetworkHandler> _logger;
    public DeleteNetworkHandler(ILogger<DeleteNetworkHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("networkName", out var nProp) || nProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: networkName");

        var networkName = nProp.GetString()!;
        var projectId = GcpClientFactory.ResolveProjectId(envVars);

        try
        {
            var networksClient = GcpClientFactory.CreateNetworks(envVars);
            var subnetsClient = GcpClientFactory.CreateSubnetworks(envVars);
            var firewallsClient = GcpClientFactory.CreateFirewalls(envVars);

            Network? network;
            try
            {
                network = await networksClient.GetAsync(projectId, networkName, ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return new LayerExecutionResult(true, $"Network '{networkName}' not found — nothing to delete.");
            }

            var networkUri = $"projects/{projectId}/global/networks/{networkName}";

            // Firewall rules attached to this network
            var firewallsDeleted = 0;
            await foreach (var fw in firewallsClient.ListAsync(projectId).WithCancellation(ct))
            {
                if (fw.Network?.EndsWith($"/networks/{networkName}") == true)
                {
                    _logger.LogInformation("  Deleting firewall rule {Firewall}", fw.Name);
                    var op = await firewallsClient.DeleteAsync(projectId, fw.Name, ct);
                    await op.PollUntilCompletedAsync();
                    firewallsDeleted++;
                }
            }

            // Subnets (across all regions)
            var subnetsDeleted = 0;
            var aggListReq = new AggregatedListSubnetworksRequest { Project = projectId };
            await foreach (var (scope, scoped) in subnetsClient.AggregatedListAsync(aggListReq).WithCancellation(ct))
            {
                if (scoped.Subnetworks is null) continue;
                foreach (var subnet in scoped.Subnetworks)
                {
                    if (subnet.Network != networkUri && !subnet.Network.EndsWith($"/networks/{networkName}"))
                        continue;
                    var region = scope.StartsWith("regions/") ? scope.Substring("regions/".Length) : scope;
                    _logger.LogInformation("  Deleting subnet {Subnet} in {Region}", subnet.Name, region);
                    var op = await subnetsClient.DeleteAsync(projectId, region, subnet.Name, ct);
                    await op.PollUntilCompletedAsync();
                    subnetsDeleted++;
                }
            }

            _logger.LogInformation("Deleting network {NetworkName}", networkName);
            var delOp = await networksClient.DeleteAsync(projectId, networkName, ct);
            await delOp.PollUntilCompletedAsync();

            return new LayerExecutionResult(true,
                $"Network '{networkName}' deleted along with {subnetsDeleted} subnet(s) and {firewallsDeleted} firewall rule(s).");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to delete network {NetworkName}", networkName);
            return new LayerExecutionResult(false, $"Failed to delete network: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }
}
