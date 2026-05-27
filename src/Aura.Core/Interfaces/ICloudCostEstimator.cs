using System.Collections.Generic;
using System.Text.Json;
using Aura.Core.Enums;

namespace Aura.Core.Interfaces;

public record RunCostEstimate(decimal TotalEstimatedCost, List<LayerCostEstimate> Layers);
public record LayerCostEstimate(string LayerName, string OperationType, decimal EstimatedCost, List<string> Breakdown);
public record LayerCostInput(string LayerName, string? OperationType, decimal DurationSeconds, JsonElement? Parameters);

public interface ICloudCostEstimator
{
    CloudProvider Provider { get; }
    RunCostEstimate EstimateRunCost(IEnumerable<LayerCostInput> layers);
}

/// <summary>
/// Selects the right ICloudCostEstimator for a given CloudProvider. Lives in
/// Aura.Core so both Api and Worker can inject it without leaking the
/// concrete impls. Falls back to Azure when an estimator isn't registered —
/// preserves prior single-provider behavior.
/// </summary>
public interface ICloudCostEstimatorFactory
{
    ICloudCostEstimator For(CloudProvider provider);
}
