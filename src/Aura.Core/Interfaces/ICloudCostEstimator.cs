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
