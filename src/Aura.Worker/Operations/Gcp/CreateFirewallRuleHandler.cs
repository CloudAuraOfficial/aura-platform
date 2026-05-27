using System.Text.Json;
using Aura.Worker.Executors;
using Google.Cloud.Compute.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// Creates an ingress firewall rule on a named network. Idempotent: if a
/// rule with the same name already exists, skip create. GCP requires per-
/// rule names (no auto-naming), so the Essence supplies ruleName.
///
/// Parameters:
///   ruleName       (required)
///   networkName    (required)
///   ports          (required) — array of strings, e.g. ["80","443","8080-8090"]
///   protocol       (optional, default "tcp")
///   sourceRanges   (optional, default ["0.0.0.0/0"])
///   targetTags     (optional) — array; if omitted rule applies to all instances
/// </summary>
public class CreateFirewallRuleHandler : IOperationHandler
{
    private readonly ILogger<CreateFirewallRuleHandler> _logger;
    public CreateFirewallRuleHandler(ILogger<CreateFirewallRuleHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("ruleName", out var rProp) || rProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: ruleName");
        if (!parameters.TryGetProperty("networkName", out var nProp) || nProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: networkName");
        if (!parameters.TryGetProperty("ports", out var pProp) || pProp.ValueKind != JsonValueKind.Array)
            return new LayerExecutionResult(false, "Missing required parameter: ports (array)");

        var ruleName = rProp.GetString()!;
        var networkName = nProp.GetString()!;
        var ports = pProp.EnumerateArray().Select(p => p.GetString()!).ToList();
        var protocol = parameters.TryGetProperty("protocol", out var prProp) ? prProp.GetString()! : "tcp";
        var sourceRanges = parameters.TryGetProperty("sourceRanges", out var srProp) && srProp.ValueKind == JsonValueKind.Array
            ? srProp.EnumerateArray().Select(s => s.GetString()!).ToList()
            : new List<string> { "0.0.0.0/0" };
        var targetTags = parameters.TryGetProperty("targetTags", out var ttProp) && ttProp.ValueKind == JsonValueKind.Array
            ? ttProp.EnumerateArray().Select(t => t.GetString()!).ToList()
            : new List<string>();

        var projectId = GcpClientFactory.ResolveProjectId(envVars);

        try
        {
            var client = GcpClientFactory.CreateFirewalls(envVars);

            try
            {
                var existing = await client.GetAsync(projectId, ruleName, ct);
                _logger.LogInformation("Firewall rule {RuleName} already exists — skipping create", ruleName);
                return new LayerExecutionResult(true, $"Firewall rule '{ruleName}' already exists on network '{networkName}'.");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // fall through to create
            }

            var firewall = new Firewall
            {
                Name = ruleName,
                Network = $"projects/{projectId}/global/networks/{networkName}",
                Direction = "INGRESS",
                Description = CreateNetworkHandler.AuraDescription(layerName),
                Allowed = { new Allowed { IPProtocol = protocol, Ports = { ports } } },
            };
            foreach (var range in sourceRanges) firewall.SourceRanges.Add(range);
            foreach (var tag in targetTags) firewall.TargetTags.Add(tag);

            _logger.LogInformation("Creating firewall rule {RuleName} on network {NetworkName} for ports {Ports}",
                ruleName, networkName, string.Join(",", ports));
            var op = await client.InsertAsync(projectId, firewall, ct);
            await op.PollUntilCompletedAsync();

            return new LayerExecutionResult(true,
                $"Firewall rule '{ruleName}' created on '{networkName}' for {protocol}:[{string.Join(",", ports)}].");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to create firewall rule {RuleName}", ruleName);
            return new LayerExecutionResult(false, $"Failed to create firewall: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }
}
