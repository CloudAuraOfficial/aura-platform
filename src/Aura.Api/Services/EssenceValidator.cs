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
    public EssenceCheck Validate(string essenceJson, CloudProvider? cloud, bool canonicalize = true) =>
        Validate(essenceJson, cloud, canonicalize, json => DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.Empty));

    // Collects every problem: shape first, then the deployment parser (the one run creation uses), then the
    // operation types. The parser only runs on input the shape check accepted, so an InvalidEssenceException
    // from it is a reason written for the user. Any other exception is a bug and propagates, so the caller
    // returns a 500 instead of an answer that looks like a verdict on the essence.
    internal EssenceCheck Validate(string essenceJson, CloudProvider? cloud, bool canonicalize,
        Func<string, List<DeploymentLayer>> parse)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(essenceJson);
        }
        catch (JsonException)
        {
            return Reject(essenceJson, "Essence JSON is not valid.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reject(essenceJson, "response is valid JSON but not an object");
            if (!root.TryGetProperty("layers", out var layers))
                return Reject(essenceJson, "JSON is valid but missing 'layers' property");
            if (layers.ValueKind != JsonValueKind.Object)
                return Reject(essenceJson, "'layers' must be an object");

            var problems = ShapeProblems(layers);
            if (problems.Count == 0)
            {
                try
                {
                    if (parse(essenceJson).Count == 0)
                        problems.Add("essence has no enabled layers");
                }
                catch (InvalidEssenceException ex)
                {
                    problems.Add(ex.Message);
                }
            }

            var outcome = Walk(root, cloud, enabledOnly: true);
            problems.AddRange(outcome.Problems);
            return Finish(essenceJson, problems, outcome.Renames, canonicalize);
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
            return new EssenceCheck(essenceJson, []);
        }

        using (doc)
        {
            var outcome = Walk(doc.RootElement, cloud: null, enabledOnly: false);
            return Finish(essenceJson, outcome.Problems, outcome.Renames, canonicalize: true);
        }
    }

    private static EssenceCheck Reject(string essenceJson, string reason) => new(essenceJson, [reason]);

    private static EssenceCheck Finish(string essenceJson, List<string> problems, List<Rename> renames, bool canonicalize)
    {
        var errors = problems.Distinct().ToList();
        if (errors.Count > 0)
            return new EssenceCheck(essenceJson, errors);
        if (!canonicalize || renames.Count == 0)
            return new EssenceCheck(essenceJson, []);

        // Stored in canonical casing; an essence that already uses it is stored exactly as sent.
        var node = JsonNode.Parse(essenceJson)!;
        foreach (var rename in renames)
        {
            var layer = node["layers"]![rename.Layer]!.AsObject();
            if (rename.InLayer)
                layer["operationType"] = rename.Canonical;
            else
                layer["parameters"]!.AsObject()["operationType"] = rename.Canonical;
        }
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return new EssenceCheck(node.ToJsonString(options), []);
    }

    // What the parser would throw on, named by layer. The parser reads isEnabled on every layer and the other
    // properties only on enabled ones, so the checks follow that: a disabled layer needs only a boolean isEnabled.
    private static List<string> ShapeProblems(JsonElement layers)
    {
        var problems = new List<string>();
        foreach (var layer in layers.EnumerateObject())
        {
            var name = layer.Name;
            var value = layer.Value;
            if (value.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"Layer '{name}' must be an object.");
                continue;
            }
            if (value.TryGetProperty("isEnabled", out var enabled)
                && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problems.Add($"Layer '{name}': isEnabled must be true or false.");
                continue;
            }
            if (!IsEnabled(value))
                continue;

            if (IsNeitherStringNorNull(value, "executorType"))
                problems.Add($"Layer '{name}': executorType must be a string.");
            if (IsNeitherStringNorNull(value, "scriptPath"))
                problems.Add($"Layer '{name}': scriptPath must be a string.");
            if (value.TryGetProperty("dependsOn", out var deps) && deps.ValueKind == JsonValueKind.Array
                && deps.EnumerateArray().Any(d => d.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                problems.Add($"Layer '{name}': dependsOn must list layer names.");

            string? operationType;
            try
            {
                operationType = DeploymentOrchestrationService.ReadLayerOperationType(name, value);
            }
            catch (InvalidEssenceException ex)
            {
                problems.Add(ex.Message);
                continue;
            }
            // The parser copies operationType into parameters, which only works on an object.
            if (operationType is not null && value.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind != JsonValueKind.Object)
                problems.Add($"Layer '{name}': parameters must be an object when operationType is set.");
        }
        return problems;
    }

    // Run creation skips a layer unless its isEnabled is the boolean true.
    private static bool IsEnabled(JsonElement layer) =>
        layer.TryGetProperty("isEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;

    private static bool IsNeitherStringNorNull(JsonElement layer, string property) =>
        layer.TryGetProperty(property, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null);

    // One pass over the layers, shared by generation, save and deployment validation. Reads each operationType the
    // way the Worker does (DeploymentOrchestrationService.ReadLayerOperationType), and checks only layers whose
    // executor uses an operation type: operation layers, and emissionload layers, which run the Azure entrypoint's list.
    // With enabledOnly, disabled layers are skipped, as run creation skips them.
    private static Outcome Walk(JsonElement root, CloudProvider? cloud, bool enabledOnly)
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
            if (enabledOnly && !IsEnabled(layer.Value))
                continue;

            string? layerLevel;
            try
            {
                layerLevel = DeploymentOrchestrationService.ReadLayerOperationType(layer.Name, layer.Value);
            }
            catch (InvalidEssenceException ex)
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
