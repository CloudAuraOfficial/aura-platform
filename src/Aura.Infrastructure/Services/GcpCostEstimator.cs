using System.Text.Json;
using Aura.Core.Enums;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Estimates GCP costs for deployment runs. Pay-as-you-go us-central1
/// rates, USD. Same record shape and switch pattern as the Azure and AWS
/// estimators so cross-provider diffing stays apples-to-apples.
/// </summary>
public class GcpCostEstimator : ICloudCostEstimator
{
    public CloudProvider Provider => CloudProvider.Gcp;

    // GCE on-demand pricing per hour (us-central1, Linux, USD)
    private static readonly Dictionary<string, decimal> GcePricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["e2-micro"]     = 0.0084m,
        ["e2-small"]     = 0.0168m,
        ["e2-medium"]    = 0.0335m,
        ["e2-standard-2"] = 0.0670m,
        ["e2-standard-4"] = 0.1340m,
        ["n2-standard-2"] = 0.0971m,
        ["n2-standard-4"] = 0.1942m,
        ["c2-standard-4"] = 0.2088m,
        ["t2d-standard-2"] = 0.0835m,
    };

    // Cloud Run per-vCPU-second + per-GB-second (us-central1)
    private const decimal CloudRunVcpuPerSecond = 0.00002400m;     // $0.000024 per vCPU-second
    private const decimal CloudRunMemoryGbPerSecond = 0.00000250m; // $0.0000025 per GB-second

    // Storage per GB per month
    private const decimal PdStandardPerGbMonth = 0.040m;  // pd-balanced default
    private const decimal GcsStandardPerGbMonth = 0.020m;

    public RunCostEstimate EstimateRunCost(IEnumerable<LayerCostInput> layers)
    {
        var layerCosts = new List<LayerCostEstimate>();
        var totalCost = 0m;

        foreach (var layer in layers)
        {
            var estimate = EstimateLayerCost(layer);
            layerCosts.Add(estimate);
            totalCost += estimate.EstimatedCost;
        }

        return new RunCostEstimate(Math.Round(totalCost, 4), layerCosts);
    }

    private static LayerCostEstimate EstimateLayerCost(LayerCostInput layer)
    {
        var durationSeconds = layer.DurationSeconds;
        var durationHours = durationSeconds / 3600m;

        var cost = 0m;
        var breakdown = new List<string>();

        switch (layer.OperationType)
        {
            case "CreateGceInstance":
                var machineType = GetParam(layer.Parameters, "machineType") ?? "e2-micro";
                var gceRate = GcePricing.GetValueOrDefault(machineType, 0.0335m);
                var gceCost = gceRate * durationHours;
                var diskGb = GetParamInt(layer.Parameters, "diskSizeGb") ?? 10;
                var diskCost = diskGb * PdStandardPerGbMonth / 730m * durationHours;
                cost = gceCost + diskCost;
                breakdown.Add($"GCE ({machineType}): ${Math.Round(gceCost, 4)} at ${gceRate}/hr × {Math.Round(durationHours, 2)}hr");
                breakdown.Add($"PD ({diskGb}GB): ${Math.Round(diskCost, 4)}");
                break;

            case "CreateGcsBucket":
                cost = 0m;
                breakdown.Add($"GCS bucket created (storage at ${GcsStandardPerGbMonth}/GB/month once populated)");
                break;

            case "DeployCloudRunService":
                // Cloud Run is request-driven; estimate idle scale-to-zero baseline.
                // If the layer ran for N seconds during deploy, the actual usage is
                // dominated by request volume, not deploy time. Show $0 with a note.
                cost = 0m;
                breakdown.Add($"Cloud Run service deployed (charged per request: ${CloudRunVcpuPerSecond}/vCPU-s + ${CloudRunMemoryGbPerSecond}/GB-s)");
                break;

            case "StartGceInstance":
            case "StopGceInstance":
            case "DeleteGceInstance":
            case "CreateNetwork":
            case "DeleteNetwork":
            case "DeleteGcsBucket":
            case "CreateFirewallRule":
            case "CreateServiceAccount":
            case "HttpHealthCheck":
                cost = 0m;
                breakdown.Add("No compute cost (management operation)");
                break;

            default:
                breakdown.Add("Unknown operation — cost not estimated");
                break;
        }

        return new LayerCostEstimate(
            layer.LayerName,
            layer.OperationType ?? "-",
            Math.Round(cost, 4),
            breakdown
        );
    }

    private static string? GetParam(JsonElement? parameters, string key)
    {
        if (parameters is null) return null;
        return parameters.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() : null;
    }

    private static int? GetParamInt(JsonElement? parameters, string key)
    {
        if (parameters is null) return null;
        if (!parameters.Value.TryGetProperty(key, out var val)) return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetInt32() : null;
    }
}
