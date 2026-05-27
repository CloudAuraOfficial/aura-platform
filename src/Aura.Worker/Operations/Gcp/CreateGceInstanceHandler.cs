using System.Text.Json;
using Aura.Worker.Executors;
using Google.Cloud.Compute.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// Launches a GCE instance into a network previously created by
/// CreateNetworkHandler. Discovers the subnet within the requested region
/// from the network's subnet list.
///
/// Parameters:
///   instanceName  (required)
///   networkName   (required)
///   zone          (required)  — full zone name, e.g. us-central1-a
///   machineType   (optional, default "e2-micro")
///   sourceImage   (optional, default Debian 12)
///   diskSizeGb    (optional, default 10)
/// </summary>
public class CreateGceInstanceHandler : IOperationHandler
{
    private readonly ILogger<CreateGceInstanceHandler> _logger;
    public CreateGceInstanceHandler(ILogger<CreateGceInstanceHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("instanceName", out var nProp) || nProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: instanceName");
        if (!parameters.TryGetProperty("networkName", out var netProp) || netProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: networkName");
        if (!parameters.TryGetProperty("zone", out var zProp) || zProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: zone");

        var instanceName = nProp.GetString()!;
        var networkName = netProp.GetString()!;
        var zone = zProp.GetString()!;
        var machineType = parameters.TryGetProperty("machineType", out var mt) ? mt.GetString()! : "e2-micro";
        var sourceImage = parameters.TryGetProperty("sourceImage", out var si)
            ? si.GetString()!
            : "projects/debian-cloud/global/images/family/debian-12";
        var diskSizeGb = parameters.TryGetProperty("diskSizeGb", out var ds) ? ds.GetInt64() : 10L;

        var projectId = GcpClientFactory.ResolveProjectId(envVars);
        var region = ZoneToRegion(zone);
        var subnetUri = $"projects/{projectId}/regions/{region}/subnetworks/{networkName}-subnet";

        try
        {
            var instancesClient = GcpClientFactory.CreateInstances(envVars);

            var instance = new Instance
            {
                Name = instanceName,
                MachineType = $"zones/{zone}/machineTypes/{machineType}",
                Description = CreateNetworkHandler.AuraDescription(layerName),
                Labels =
                {
                    ["aura-layer"] = SanitizeLabel(layerName),
                    ["aura-managed"] = "true",
                },
                Disks =
                {
                    new AttachedDisk
                    {
                        Boot = true,
                        AutoDelete = true,
                        InitializeParams = new AttachedDiskInitializeParams
                        {
                            SourceImage = sourceImage,
                            DiskSizeGb = diskSizeGb,
                        },
                    },
                },
                NetworkInterfaces =
                {
                    new NetworkInterface
                    {
                        Network = $"projects/{projectId}/global/networks/{networkName}",
                        Subnetwork = subnetUri,
                        AccessConfigs = { new AccessConfig { Name = "External NAT", Type = "ONE_TO_ONE_NAT" } },
                    },
                },
            };

            _logger.LogInformation("Launching GCE {InstanceName} ({MachineType}) in {Zone}", instanceName, machineType, zone);
            var op = await instancesClient.InsertAsync(projectId, zone, instance, ct);
            await op.PollUntilCompletedAsync();

            var fresh = await instancesClient.GetAsync(projectId, zone, instanceName, ct);
            var externalIp = fresh.NetworkInterfaces.FirstOrDefault()?.AccessConfigs.FirstOrDefault()?.NatIP ?? "pending";

            return new LayerExecutionResult(true,
                $"GCE instance '{instanceName}' launched: id={fresh.Id}, zone={zone}, externalIp={externalIp}.");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to launch GCE {InstanceName}", instanceName);
            return new LayerExecutionResult(false, $"Failed to launch GCE: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }

    private static string ZoneToRegion(string zone)
    {
        // zone like "us-central1-a" → region "us-central1"
        var lastDash = zone.LastIndexOf('-');
        return lastDash > 0 ? zone[..lastDash] : zone;
    }

    // GCP labels are restricted to [a-z0-9_-], max 63 chars
    private static string SanitizeLabel(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '-').ToArray())
            .TrimStart('-')[..Math.Min(63, s.Length)];
}
