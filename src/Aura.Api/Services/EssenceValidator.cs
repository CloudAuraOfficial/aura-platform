using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Operations;
using Aura.Infrastructure.Services;

namespace Aura.Api.Services;

public sealed class EssenceValidator : IEssenceValidator
{
    public EssenceCheck Validate(string essenceJson, CloudProvider cloud) =>
        Validate(essenceJson, cloud, json => DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.Empty));

    // Uses the same parser deployments use, so an essence we return is one a run can actually be
    // created from: a cycle, an unknown runPolicy or executor, or an empty layer set would otherwise
    // only fail later, at run creation. Any parser exception counts as an invalid output. Letting one
    // escape would skip the retry loop and the usage row.
    public EssenceCheck CheckRunnable(string essenceJson) =>
        Validate(essenceJson, null, json => DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.Empty));

    // A null cloud skips the cloud-scope check; the EmissionLoad and unknown-type checks still apply.
    internal EssenceCheck Validate(string essenceJson, CloudProvider? cloud, Func<string, List<DeploymentLayer>> parse)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(essenceJson);
        }
        catch (JsonException ex)
        {
            return Reject(essenceJson, ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reject(essenceJson, "response is valid JSON but not an object");
            if (!root.TryGetProperty("layers", out _))
                return Reject(essenceJson, "JSON is valid but missing 'layers' property");

            try
            {
                var layers = parse(essenceJson);
                if (layers.Count == 0)
                    return Reject(essenceJson, "essence has no enabled layers");
            }
            catch (Exception ex)
            {
                return Reject(essenceJson, ex.Message);
            }

            return Finish(essenceJson, Walk(root, cloud));
        }
    }

    public EssenceCheck CheckOperationTypes(string essenceJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(essenceJson);
        }
        catch (JsonException)
        {
            return new EssenceCheck(essenceJson, null);
        }

        using (doc)
        {
            return Finish(essenceJson, Walk(doc.RootElement, cloud: null));
        }
    }

    private static EssenceCheck Reject(string essenceJson, string reason) => new(essenceJson, reason);

    private static EssenceCheck Finish(string essenceJson, Outcome outcome)
    {
        if (outcome.Problems.Count > 0)
            return Reject(essenceJson, string.Join(" ", outcome.Problems));
        if (outcome.Renames.Count == 0)
            return new EssenceCheck(essenceJson, null);

        // Stored in canonical casing; an essence that already uses it is stored exactly as sent.
        var node = JsonNode.Parse(essenceJson)!;
        foreach (var rename in outcome.Renames)
        {
            var layer = node["layers"]![rename.Layer]!.AsObject();
            if (rename.InLayer)
                layer["operationType"] = rename.Canonical;
            else
                layer["parameters"]!.AsObject()["operationType"] = rename.Canonical;
        }
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return new EssenceCheck(node.ToJsonString(options), null);
    }

    // One pass over the layers, shared by generation and save. Reads each operationType the way the Worker
    // does (DeploymentOrchestrationService.ReadLayerOperationType), and checks only layers whose executor
    // uses an operation type: operation layers, and emissionload layers, which run the Azure entrypoint's list.
    private static Outcome Walk(JsonElement root, CloudProvider? cloud)
    {
        var problems = new List<string>();
        var renames = new List<Rename>();

        // TryGetProperty throws on a non-object root, so the root kind is checked first.
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("layers", out var layers)
            || layers.ValueKind != JsonValueKind.Object)
            return new Outcome(problems, renames);

        var containerCloud = ContainerCloud(root);
        var unknown = new List<string>();
        var outOfScope = new List<string>();
        var notRun = new List<string>();

        foreach (var layer in layers.EnumerateObject())
        {
            if (layer.Value.ValueKind != JsonValueKind.Object)
                continue;

            string? layerLevel;
            try
            {
                layerLevel = DeploymentOrchestrationService.ReadLayerOperationType(layer.Name, layer.Value);
            }
            catch (InvalidOperationException ex)
            {
                problems.Add(ex.Message);
                continue;
            }

            var executorType = layer.Value.TryGetProperty("executorType", out var exec) && exec.ValueKind == JsonValueKind.String
                ? exec.GetString() ?? ""
                : "";
            var isEmission = executorType.Equals("emissionload", StringComparison.OrdinalIgnoreCase);
            // Same rule as ParseAndSortLayers: a layer with an operationType and no executorType is an operation.
            var isOperation = executorType.Equals("operation", StringComparison.OrdinalIgnoreCase)
                || (executorType.Length == 0 && layerLevel is not null);

            // Script layers: parameters.operationType is a plain argument, not checked.
            if (!isEmission && !isOperation)
                continue;

            var operationType = layerLevel;
            var inLayer = operationType is not null;
            if (operationType is null
                && layer.Value.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("operationType", out var paramOp)
                && paramOp.ValueKind != JsonValueKind.Null)
            {
                // The Worker falls back to parameters.operationType when the layer-level value is absent.
                if (paramOp.ValueKind != JsonValueKind.String)
                {
                    problems.Add($"Layer '{layer.Name}': parameters.operationType must be a string.");
                    continue;
                }
                operationType = string.IsNullOrEmpty(paramOp.GetString()) ? null : paramOp.GetString();
            }

            if (operationType is null)
                continue;

            var canonical = OperationTypes.Canonicalize(operationType);
            if (canonical is null)
            {
                unknown.Add($"layer '{layer.Name}' uses '{operationType}'");
                continue;
            }
            if (canonical != operationType)
                renames.Add(new Rename(layer.Name, inLayer, canonical));

            if (isEmission)
            {
                if (!OperationTypes.EmissionLoadEntrypoint(containerCloud).Contains(canonical))
                    notRun.Add($"layer '{layer.Name}' uses '{canonical}'");
            }
            else if (cloud is { } target && !OperationTypes.ForCloud(target).Contains(canonical))
            {
                outOfScope.Add($"layer '{layer.Name}' uses '{canonical}'");
            }
        }

        if (unknown.Count > 0)
        {
            var valid = cloud is { } c ? OperationTypes.ForCloud(c) : OperationTypes.All;
            problems.Add($"Unknown operationType: {string.Join("; ", unknown)}. " +
                         $"Valid types: {string.Join(", ", valid)}.");
        }
        if (outOfScope.Count > 0)
        {
            var target = cloud!.Value;
            problems.Add($"Not an {target} operation type: {string.Join("; ", outOfScope)}. " +
                         $"Valid {target} types: {string.Join(", ", OperationTypes.ForCloud(target))}.");
        }
        if (notRun.Count > 0)
        {
            var supported = OperationTypes.EmissionLoadEntrypoint(containerCloud);
            problems.Add($"The EmissionLoad {containerCloud} container cannot run: {string.Join("; ", notRun)}. " +
                         $"It runs: {(supported.Count == 0 ? "none" : string.Join(", ", supported))}.");
        }

        return new Outcome(problems, renames);
    }

    // The cloud whose EmissionLoad container runs the layers, as the Worker resolves it (baseEssence.cloudProvider, default Azure).
    private static CloudProvider ContainerCloud(JsonElement root)
    {
        try
        {
            return EmissionLoadResolver.ParseBaseEssence(root.GetRawText()).Provider;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return CloudProvider.Azure;
        }
    }

    private sealed record Rename(string Layer, bool InLayer, string Canonical);

    private sealed record Outcome(List<string> Problems, List<Rename> Renames);
}
