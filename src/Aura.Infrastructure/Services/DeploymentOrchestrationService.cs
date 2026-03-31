using System.Diagnostics;
using System.Text.Json;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class DeploymentOrchestrationService : IDeploymentOrchestrationService
{
    private readonly AuraDbContext _db;
    private readonly ILogger<DeploymentOrchestrationService> _logger;

    public DeploymentOrchestrationService(AuraDbContext db, ILogger<DeploymentOrchestrationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DeploymentRun> CreateRunAsync(Deployment deployment, CancellationToken ct = default)
    {
        var essence = await _db.Essences
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == deployment.EssenceId, ct)
            ?? throw new InvalidOperationException($"Essence {deployment.EssenceId} not found.");

        // Freeze the current essence state as an immutable snapshot
        var snapshotJson = essence.EssenceJson;

        var run = new DeploymentRun
        {
            TenantId = deployment.TenantId,
            DeploymentId = deployment.Id,
            Status = RunStatus.Queued,
            SnapshotJson = snapshotJson,
            TraceParent = Activity.Current?.Id
        };
        _db.DeploymentRuns.Add(run);

        // Parse layers from the snapshot and build execution order
        var layers = ParseAndSortLayers(snapshotJson, run.Id);
        foreach (var layer in layers)
            _db.DeploymentLayers.Add(layer);

        await _db.SaveChangesAsync(ct);

        run.Layers = layers;
        _logger.LogInformation(
            "Created run {RunId} for deployment {DeploymentId} with {LayerCount} layers, essence {EssenceId}, snapshot size {SnapshotSize} bytes",
            run.Id, deployment.Id, layers.Count, deployment.EssenceId, snapshotJson.Length);

        return run;
    }

    internal static List<DeploymentLayer> ParseAndSortLayers(string snapshotJson, Guid runId)
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("layers", out var layersElement))
            return [];

        // Parse each layer definition
        var definitions = new Dictionary<string, LayerDefinition>();
        foreach (var prop in layersElement.EnumerateObject())
        {
            var name = prop.Name;
            var val = prop.Value;

            var isEnabled = val.TryGetProperty("isEnabled", out var enabledProp) && enabledProp.GetBoolean();
            if (!isEnabled)
                continue;

            var executorTypeStr = val.TryGetProperty("executorType", out var execProp)
                ? execProp.GetString() ?? ""
                : "";

            var operationType = val.TryGetProperty("operationType", out var opTypeProp)
                ? opTypeProp.GetString()
                : null;

            ExecutorType executorType;
            if (!string.IsNullOrEmpty(operationType) && string.IsNullOrEmpty(executorTypeStr))
                executorType = ExecutorType.Operation;
            else
                executorType = ParseExecutorType(string.IsNullOrEmpty(executorTypeStr) ? "powershell" : executorTypeStr);

            var parameters = val.TryGetProperty("parameters", out var paramsProp)
                ? paramsProp.GetRawText()
                : "{}";

            // Inject operationType into parameters so OperationExecutor can read it
            if (!string.IsNullOrEmpty(operationType))
            {
                using var paramDoc = JsonDocument.Parse(parameters);
                var dict = new Dictionary<string, JsonElement>();
                foreach (var p in paramDoc.RootElement.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();

                if (!dict.ContainsKey("operationType"))
                {
                    using var stream = new System.IO.MemoryStream();
                    using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("operationType", operationType);
                        foreach (var kvp in dict)
                        {
                            writer.WritePropertyName(kvp.Key);
                            kvp.Value.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                    parameters = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }
            }

            var scriptPath = val.TryGetProperty("scriptPath", out var scriptProp)
                ? scriptProp.GetString()
                : null;

            var dependsOn = new List<string>();
            if (val.TryGetProperty("dependsOn", out var depsProp) && depsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in depsProp.EnumerateArray())
                {
                    var depName = dep.GetString();
                    if (depName is not null)
                        dependsOn.Add(depName);
                }
            }

            definitions[name] = new LayerDefinition(name, executorType, parameters, scriptPath, dependsOn, operationType);
        }

        // Remove dependencies on disabled/missing layers
        var validNames = definitions.Keys.ToHashSet();
        foreach (var def in definitions.Values)
            def.DependsOn.RemoveAll(d => !validNames.Contains(d));

        // Topological sort (Kahn's algorithm)
        var sorted = TopologicalSort(definitions);

        // Build DeploymentLayer entities
        var result = new List<DeploymentLayer>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var def = sorted[i];
            result.Add(new DeploymentLayer
            {
                RunId = runId,
                LayerName = def.Name,
                ExecutorType = def.ExecutorType,
                Status = LayerStatus.Pending,
                Parameters = def.Parameters,
                ScriptPath = def.ScriptPath,
                OperationType = def.OperationType,
                DependsOn = JsonSerializer.Serialize(def.DependsOn),
                SortOrder = i
            });
        }

        return result;
    }

    internal static List<LayerDefinition> TopologicalSort(Dictionary<string, LayerDefinition> definitions)
    {
        var inDegree = definitions.ToDictionary(kv => kv.Key, _ => 0);
        var adjacency = definitions.ToDictionary(kv => kv.Key, _ => new List<string>());

        foreach (var (name, def) in definitions)
        {
            foreach (var dep in def.DependsOn)
            {
                adjacency[dep].Add(name);
                inDegree[name]++;
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(n => n));
        var sorted = new List<LayerDefinition>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(definitions[current]);

            foreach (var neighbor in adjacency[current].OrderBy(n => n))
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (sorted.Count != definitions.Count)
            throw new InvalidOperationException("Cycle detected in layer dependencies.");

        return sorted;
    }

    private static ExecutorType ParseExecutorType(string value) => value.ToLowerInvariant() switch
    {
        "powershell" => ExecutorType.PowerShell,
        "python" => ExecutorType.Python,
        "csharp_sdk" => ExecutorType.CSharpSdk,
        "operation" => ExecutorType.Operation,
        "emissionload" => ExecutorType.EmissionLoad,
        _ => throw new InvalidOperationException($"Unknown executor type: {value}")
    };

    internal record LayerDefinition(
        string Name,
        ExecutorType ExecutorType,
        string Parameters,
        string? ScriptPath,
        List<string> DependsOn,
        string? OperationType = null
    );
}
