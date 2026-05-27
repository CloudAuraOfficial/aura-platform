using System.Text.Json;
using Aura.Core.Enums;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Estimates AWS costs for deployment runs. Pay-as-you-go rates, us-east-1,
/// USD. Approximate projections — not invoices. Same record shape and switch
/// pattern as AzureCostEstimator so future cost diffing across providers is
/// apples-to-apples.
/// </summary>
public class AwsCostEstimator : ICloudCostEstimator
{
    public CloudProvider Provider => CloudProvider.Aws;

    // On-demand EC2 Linux pricing per hour (us-east-1, USD)
    private static readonly Dictionary<string, decimal> Ec2Pricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["t3.nano"]    = 0.0052m,
        ["t3.micro"]   = 0.0104m,
        ["t3.small"]   = 0.0208m,
        ["t3.medium"]  = 0.0416m,
        ["t3.large"]   = 0.0832m,
        ["t3.xlarge"]  = 0.1664m,
        ["t3a.micro"]  = 0.0094m,
        ["t3a.small"]  = 0.0188m,
        ["m5.large"]   = 0.096m,
        ["m5.xlarge"]  = 0.192m,
        ["m5.2xlarge"] = 0.384m,
        ["c5.large"]   = 0.085m,
        ["c5.xlarge"]  = 0.17m,
        ["r5.large"]   = 0.126m,
    };

    // S3 Standard storage per GB per month (us-east-1)
    private const decimal S3StandardPerGbMonth = 0.023m;

    // Fargate per-second pricing (us-east-1, Linux/X86)
    private const decimal FargateVcpuPerSecond = 0.00001124m;     // $0.04048 per vCPU-hour / 3600
    private const decimal FargateMemoryGbPerSecond = 0.00000123m; // $0.004445 per GB-hour / 3600

    // EBS gp3 storage per GB per month
    private const decimal EbsGp3PerGbMonth = 0.08m;

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
            case "CreateEc2Instance":
                var instanceType = GetParam(layer.Parameters, "instanceType") ?? "t3.micro";
                var ec2Rate = Ec2Pricing.GetValueOrDefault(instanceType, 0.0416m);
                var ec2Cost = ec2Rate * durationHours;
                // EBS root volume (default 8 GB gp3 unless overridden)
                var ebsGb = GetParamInt(layer.Parameters, "rootVolumeSizeGb") ?? 8;
                var ebsCost = ebsGb * EbsGp3PerGbMonth / 730m * durationHours; // monthly → hourly
                cost = ec2Cost + ebsCost;
                breakdown.Add($"EC2 ({instanceType}): ${Math.Round(ec2Cost, 4)} at ${ec2Rate}/hr × {Math.Round(durationHours, 2)}hr");
                breakdown.Add($"EBS gp3 ({ebsGb}GB): ${Math.Round(ebsCost, 4)}");
                break;

            case "CreateS3Bucket":
                // Bucket creation has no storage cost until objects land — return $0
                // with a clarifying breakdown note so the dashboard isn't misleading.
                cost = 0m;
                breakdown.Add($"S3 bucket created (storage at ${S3StandardPerGbMonth}/GB/month once populated)");
                break;

            case "RunEcsTask":
                // Fargate billing requires cpu+memory pulled from the task definition;
                // here we only have layer params so use overrides if supplied.
                var cpu = GetParamDouble(layer.Parameters, "cpuUnits") ?? 256.0;  // 0.25 vCPU
                var memMb = GetParamDouble(layer.Parameters, "memoryMb") ?? 512.0;
                var cpuVal = (decimal)(cpu / 1024.0) * FargateVcpuPerSecond * durationSeconds;
                var memVal = (decimal)(memMb / 1024.0) * FargateMemoryGbPerSecond * durationSeconds;
                cost = cpuVal + memVal;
                breakdown.Add($"Fargate ({cpu} CPU units, {memMb}MB) × {durationSeconds}s = ${Math.Round(cost, 4)}");
                break;

            case "DeployCloudFormation":
                cost = 0m;
                breakdown.Add("CloudFormation itself is free — resources inside the stack billed separately.");
                break;

            case "StartEc2Instance":
            case "StopEc2Instance":
            case "TerminateEc2Instance":
            case "CreateVpc":
            case "DeleteVpc":
            case "DeleteS3Bucket":
            case "CreateIamRole":
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

    private static double? GetParamDouble(JsonElement? parameters, string key)
    {
        if (parameters is null) return null;
        if (!parameters.Value.TryGetProperty(key, out var val)) return null;
        return val.ValueKind == JsonValueKind.Number ? val.GetDouble() : null;
    }
}
