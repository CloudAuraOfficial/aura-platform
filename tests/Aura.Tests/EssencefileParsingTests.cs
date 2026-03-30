using System.Text.Json;
using Aura.Core.Enums;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

public class EssencefileParsingTests
{
    private static string LoadAciEssencefile()
    {
        // Navigate from test bin directory up to the repo root
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "Essences", "Aura", "aci-deploy", "Essencefile.json");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ParseAndSortLayers_From_Essencefile_CreatesCorrectLayers()
    {
        var json = LoadAciEssencefile();
        var runId = Guid.NewGuid();

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, runId);

        // 11 total in file (9 enabled + 2 disabled), only 9 returned
        Assert.Equal(9, layers.Count);

        // All layers should have ExecutorType.Operation
        Assert.All(layers, l => Assert.Equal(ExecutorType.Operation, l.ExecutorType));

        // ResourceGroup has SortOrder 0 (no dependencies)
        var rg = layers.Single(l => l.LayerName == "ResourceGroup");
        Assert.Equal(0, rg.SortOrder);

        // DeployContainerGroup depends on PushApiImage, ImportPostgresImage, ImportRedisImage
        var dcg = layers.Single(l => l.LayerName == "DeployContainerGroup");
        var dcgDeps = JsonSerializer.Deserialize<List<string>>(dcg.DependsOn)!;
        Assert.Contains("PushApiImage", dcgDeps);
        Assert.Contains("ImportPostgresImage", dcgDeps);
        Assert.Contains("ImportRedisImage", dcgDeps);

        // HealthCheck comes after DeployContainerGroup
        var hc = layers.Single(l => l.LayerName == "HealthCheck");
        Assert.True(hc.SortOrder > dcg.SortOrder);

        // SmokeTest comes after HealthCheck
        var st = layers.Single(l => l.LayerName == "SmokeTest");
        Assert.True(st.SortOrder > hc.SortOrder);

        // Disabled layers (StopContainerGroup, DeleteContainerGroup) are excluded
        Assert.DoesNotContain(layers, l => l.LayerName == "StopContainerGroup");
        Assert.DoesNotContain(layers, l => l.LayerName == "DeleteContainerGroup");

        // All layers belong to the correct run
        Assert.All(layers, l => Assert.Equal(runId, l.RunId));
    }

    [Fact]
    public void ParseAndSortLayers_OperationType_Preserved()
    {
        var json = LoadAciEssencefile();
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());

        // Every enabled layer in the ACI essencefile has an operationType
        Assert.All(layers, l => Assert.False(string.IsNullOrEmpty(l.OperationType)));

        // Verify specific operation types
        Assert.Equal("CreateResourceGroup", layers.Single(l => l.LayerName == "ResourceGroup").OperationType);
        Assert.Equal("CreateContainerRegistry", layers.Single(l => l.LayerName == "ContainerRegistry").OperationType);
        Assert.Equal("BuildContainerImage", layers.Single(l => l.LayerName == "BuildApiImage").OperationType);
        Assert.Equal("PushContainerImage", layers.Single(l => l.LayerName == "PushApiImage").OperationType);
        Assert.Equal("ImportContainerImage", layers.Single(l => l.LayerName == "ImportPostgresImage").OperationType);
        Assert.Equal("ImportContainerImage", layers.Single(l => l.LayerName == "ImportRedisImage").OperationType);
        Assert.Equal("CreateContainerGroup", layers.Single(l => l.LayerName == "DeployContainerGroup").OperationType);
        Assert.Equal("HttpHealthCheck", layers.Single(l => l.LayerName == "HealthCheck").OperationType);
        Assert.Equal("HttpHealthCheck", layers.Single(l => l.LayerName == "SmokeTest").OperationType);
    }

    [Fact]
    public void ParseAndSortLayers_MixedEssence_BackwardCompatible()
    {
        var essenceJson = JsonSerializer.Serialize(new
        {
            layers = new Dictionary<string, object>
            {
                ["ScriptLayer"] = new
                {
                    isEnabled = true,
                    executorType = "powershell",
                    parameters = new { rgName = "MyRG" },
                    scriptPath = "scripts/rg.ps1"
                },
                ["OperationLayer"] = new
                {
                    isEnabled = true,
                    operationType = "CreateVM",
                    parameters = new { vmName = "TestVM" },
                    dependsOn = new[] { "ScriptLayer" }
                }
            }
        });

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(essenceJson, Guid.NewGuid());

        Assert.Equal(2, layers.Count);

        // Script-based layer uses its executor type
        var scriptLayer = layers.Single(l => l.LayerName == "ScriptLayer");
        Assert.Equal(ExecutorType.PowerShell, scriptLayer.ExecutorType);
        Assert.Null(scriptLayer.OperationType);
        Assert.Equal("scripts/rg.ps1", scriptLayer.ScriptPath);

        // Operation-based layer gets ExecutorType.Operation
        var opLayer = layers.Single(l => l.LayerName == "OperationLayer");
        Assert.Equal(ExecutorType.Operation, opLayer.ExecutorType);
        Assert.Equal("CreateVM", opLayer.OperationType);
    }
}
