using System.Text.Json;
using Aura.Core.Entities;
using Aura.Core.Operations;
using Aura.Infrastructure.Services;

namespace Aura.Api.Services;

public sealed class EssenceValidator : IEssenceValidator
{
    public string? Validate(string essenceJson) =>
        Validate(essenceJson, json => DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.Empty));

    // Uses the same parser deployments use, so an essence we return is one a run can actually be
    // created from: a cycle, an unknown runPolicy or executor, or an empty layer set would otherwise
    // only fail later, at run creation. Any parser exception counts as an invalid output. Letting one
    // escape would skip the retry loop and the usage row.
    internal string? Validate(string essenceJson, Func<string, List<DeploymentLayer>> parse)
    {
        try
        {
            using var doc = JsonDocument.Parse(essenceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "response is valid JSON but not an object";
            if (!doc.RootElement.TryGetProperty("layers", out _))
                return "JSON is valid but missing 'layers' property";
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }

        try
        {
            var layers = parse(essenceJson);
            if (layers.Count == 0)
                return "essence has no enabled layers";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        return ValidateOperationTypes(essenceJson);
    }

    public string? ValidateOperationTypes(string essenceJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(essenceJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("layers", out var layers)
                || layers.ValueKind != JsonValueKind.Object)
                return null;

            var unknown = new List<string>();
            foreach (var layer in layers.EnumerateObject())
            {
                var operationType = ReadOperationType(layer.Value);
                if (operationType is not null && !OperationTypes.IsValid(operationType))
                    unknown.Add($"layer '{layer.Name}' uses '{operationType}'");
            }

            if (unknown.Count == 0)
                return null;

            return $"Unknown operationType: {string.Join("; ", unknown)}. " +
                   $"Valid types: {string.Join(", ", OperationTypes.All)}.";
        }
    }

    // Mirrors what the Worker executes: the layer's operationType, else parameters.operationType.
    // A non-string or empty value is treated as absent, as DeploymentOrchestrationService does.
    private static string? ReadOperationType(JsonElement layer)
    {
        if (layer.ValueKind != JsonValueKind.Object)
            return null;

        if (layer.TryGetProperty("operationType", out var layerOp) && layerOp.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(layerOp.GetString()))
            return layerOp.GetString();

        if (layer.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("operationType", out var paramOp) && paramOp.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(paramOp.GetString()))
            return paramOp.GetString();

        return null;
    }
}
