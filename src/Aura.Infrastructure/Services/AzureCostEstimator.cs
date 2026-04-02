using System.Text.Json;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Estimates Azure costs for deployment runs based on operation types,
/// resource parameters, and actual execution durations.
/// Uses Azure pay-as-you-go rates (USD). Rates are approximate and
/// should be treated as projections, not invoices.
/// </summary>
public static class AzureCostEstimator
{
    // VM pricing per hour (pay-as-you-go, Linux, USD)
    private static readonly Dictionary<string, decimal> VmPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Standard_B1s"] = 0.0104m,
        ["Standard_B1ms"] = 0.0207m,
        ["Standard_B2s"] = 0.0416m,
        ["Standard_B2ms"] = 0.0832m,
        ["Standard_D2s_v3"] = 0.096m,
        ["Standard_D2s_v5"] = 0.096m,
        ["Standard_D4s_v3"] = 0.192m,
        ["Standard_D4s_v5"] = 0.192m,
        ["Standard_D8s_v3"] = 0.384m,
        ["Standard_D2as_v4"] = 0.096m,
        ["Standard_E2s_v3"] = 0.126m,
        ["Standard_F2s_v2"] = 0.085m,
    };

    // ACI pricing per second
    private const decimal AciCpuPerSecond = 0.0000135m;
    private const decimal AciMemoryPerGbPerSecond = 0.0000015m;

    // Other fixed costs
    private const decimal PublicIpPerHour = 0.005m;
    private const decimal ManagedDiskPerGbPerMonth = 0.05m;

    public static RunCostEstimate EstimateRunCost(
        IEnumerable<LayerCostInput> layers)
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
            case "CreateVM":
            case "DeployArmTemplate" when HasVmParams(layer.Parameters):
                var vmSize = GetParam(layer.Parameters, "vmSize")
                    ?? GetNestedParam(layer.Parameters, "templateParameters", "vmSize")
                    ?? "Standard_D2s_v3";
                var osDiskGb = GetParamInt(layer.Parameters, "osDiskSizeGB")
                    ?? GetNestedParamInt(layer.Parameters, "templateParameters", "osDiskSizeGB")
                    ?? 30;

                var vmRate = VmPricing.GetValueOrDefault(vmSize, 0.096m);
                var vmCost = vmRate * durationHours;
                var diskCost = (osDiskGb * ManagedDiskPerGbPerMonth / 730m) * durationHours; // pro-rate monthly to hourly
                var ipCost = PublicIpPerHour * durationHours;

                cost = vmCost + diskCost + ipCost;
                breakdown.Add($"VM ({vmSize}): ${Math.Round(vmCost, 4)}/hr x {Math.Round(durationHours, 2)}hr");
                breakdown.Add($"Disk ({osDiskGb}GB): ${Math.Round(diskCost, 4)}");
                breakdown.Add($"Public IP: ${Math.Round(ipCost, 4)}");
                break;

            case "CreateContainerGroup":
                var containers = GetContainersFromParams(layer.Parameters);
                foreach (var c in containers)
                {
                    var cpuCost = (decimal)c.Cpu * AciCpuPerSecond * durationSeconds;
                    var memCost = (decimal)c.MemoryGb * AciMemoryPerGbPerSecond * durationSeconds;
                    cost += cpuCost + memCost;
                    breakdown.Add($"ACI {c.Name}: {c.Cpu} vCPU + {c.MemoryGb}GB = ${Math.Round(cpuCost + memCost, 4)}");
                }
                break;

            case "CreateContainerRegistry":
                var sku = GetParam(layer.Parameters, "sku") ?? "Basic";
                var dailyRate = sku switch
                {
                    "Basic" => 0.167m,
                    "Standard" => 0.667m,
                    "Premium" => 1.667m,
                    _ => 0.167m
                };
                cost = dailyRate * (durationHours / 24m);
                breakdown.Add($"ACR ({sku}): ${dailyRate}/day");
                break;

            case "StopVM":
            case "StartVM":
            case "DeleteVM":
            case "CreateResourceGroup":
            case "DeleteResourceGroup":
            case "StopContainerGroup":
            case "DeleteContainerGroup":
            case "HttpHealthCheck":
                // Management operations — no direct cost
                cost = 0m;
                breakdown.Add("No compute cost (management operation)");
                break;

            case "BuildContainerImage":
                // ACR Build: ~$0.0001/second for build compute
                cost = 0.0001m * durationSeconds;
                breakdown.Add($"ACR Build: ${Math.Round(cost, 4)} ({durationSeconds}s)");
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

    private static bool HasVmParams(JsonElement? parameters)
    {
        if (parameters is null) return false;
        return parameters.Value.TryGetProperty("templateParameters", out var tp)
            && tp.TryGetProperty("vmSize", out _);
    }

    private static string? GetParam(JsonElement? parameters, string key)
    {
        if (parameters is null) return null;
        return parameters.Value.TryGetProperty(key, out var val) ? val.GetString() : null;
    }

    private static int? GetParamInt(JsonElement? parameters, string key)
    {
        if (parameters is null) return null;
        if (!parameters.Value.TryGetProperty(key, out var val)) return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetInt32() : null;
    }

    private static string? GetNestedParam(JsonElement? parameters, string parent, string key)
    {
        if (parameters is null) return null;
        if (!parameters.Value.TryGetProperty(parent, out var p)) return null;
        return p.TryGetProperty(key, out var val) ? val.GetString() : null;
    }

    private static int? GetNestedParamInt(JsonElement? parameters, string parent, string key)
    {
        if (parameters is null) return null;
        if (!parameters.Value.TryGetProperty(parent, out var p)) return null;
        if (!p.TryGetProperty(key, out var val)) return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetInt32() : null;
    }

    private static List<ContainerSpec> GetContainersFromParams(JsonElement? parameters)
    {
        var result = new List<ContainerSpec>();
        if (parameters is null) return result;
        if (!parameters.Value.TryGetProperty("containers", out var containers)) return result;
        if (containers.ValueKind != JsonValueKind.Array) return result;

        foreach (var c in containers.EnumerateArray())
        {
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "container" : "container";
            var cpu = c.TryGetProperty("cpu", out var cpuVal) ? cpuVal.GetDouble() : 0.25;
            var mem = c.TryGetProperty("memoryInGB", out var memVal) ? memVal.GetDouble() : 0.5;
            result.Add(new ContainerSpec(name, cpu, mem));
        }
        return result;
    }

    private record ContainerSpec(string Name, double Cpu, double MemoryGb);
}

public record RunCostEstimate(decimal TotalEstimatedCost, List<LayerCostEstimate> Layers);
public record LayerCostEstimate(string LayerName, string OperationType, decimal EstimatedCost, List<string> Breakdown);
public record LayerCostInput(string LayerName, string? OperationType, decimal DurationSeconds, JsonElement? Parameters);
