using System.Text.Json;
using Aura.Core.Enums;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

public class SnapshotParsingTests
{
    [Fact]
    public void ParseAndSortLayers_FromEssenceJson_CreatesOrderedLayers()
    {
        var essenceJson = JsonSerializer.Serialize(new
        {
            baseEssence = new { EmissionLoadName = "Test", cloudProvider = "Azure" },
            layers = new Dictionary<string, object>
            {
                ["CreateRG"] = new
                {
                    isEnabled = true,
                    executorType = "powershell",
                    parameters = new { rgName = "MyRG" },
                    scriptPath = "scripts/rg.ps1"
                },
                ["CreateVM"] = new
                {
                    isEnabled = true,
                    executorType = "csharp_sdk",
                    parameters = new { vmName = "MyVM" },
                    dependsOn = new[] { "CreateRG" }
                },
                ["StartVM"] = new
                {
                    isEnabled = true,
                    executorType = "python",
                    parameters = new { vmName = "MyVM" },
                    scriptPath = "scripts/start.py",
                    dependsOn = new[] { "CreateVM" }
                }
            }
        });

        var runId = Guid.NewGuid();
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(essenceJson, runId);

        Assert.Equal(3, layers.Count);
        Assert.Equal("CreateRG", layers[0].LayerName);
        Assert.Equal("CreateVM", layers[1].LayerName);
        Assert.Equal("StartVM", layers[2].LayerName);

        Assert.Equal(ExecutorType.PowerShell, layers[0].ExecutorType);
        Assert.Equal(ExecutorType.CSharpSdk, layers[1].ExecutorType);
        Assert.Equal(ExecutorType.Python, layers[2].ExecutorType);

        Assert.Equal(0, layers[0].SortOrder);
        Assert.Equal(1, layers[1].SortOrder);
        Assert.Equal(2, layers[2].SortOrder);

        Assert.Equal("scripts/rg.ps1", layers[0].ScriptPath);
        Assert.Null(layers[1].ScriptPath);
        Assert.Equal("scripts/start.py", layers[2].ScriptPath);

        Assert.All(layers, l => Assert.Equal(runId, l.RunId));
        Assert.All(layers, l => Assert.Equal(LayerStatus.Pending, l.Status));
    }

    [Fact]
    public void ParseAndSortLayers_DisabledLayersExcluded()
    {
        var essenceJson = JsonSerializer.Serialize(new
        {
            layers = new Dictionary<string, object>
            {
                ["A"] = new { isEnabled = true, executorType = "powershell" },
                ["B"] = new { isEnabled = false, executorType = "python", dependsOn = new[] { "A" } },
                ["C"] = new { isEnabled = true, executorType = "python", dependsOn = new[] { "B" } }
            }
        });

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(essenceJson, Guid.NewGuid());

        Assert.Equal(2, layers.Count);
        Assert.Equal("A", layers[0].LayerName);
        Assert.Equal("C", layers[1].LayerName);
        // C's dependsOn "B" should be stripped since B is disabled
        Assert.Equal("[]", layers[1].DependsOn);
    }

    [Fact]
    public void ParseAndSortLayers_NoLayersProperty_ReturnsEmpty()
    {
        var essenceJson = """{"baseEssence": {"name": "test"}}""";
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(essenceJson, Guid.NewGuid());
        Assert.Empty(layers);
    }

    [Fact]
    public void ParseAndSortLayers_CyclicDependency_Throws()
    {
        var essenceJson = JsonSerializer.Serialize(new
        {
            layers = new Dictionary<string, object>
            {
                ["A"] = new { isEnabled = true, executorType = "powershell", dependsOn = new[] { "B" } },
                ["B"] = new { isEnabled = true, executorType = "powershell", dependsOn = new[] { "A" } }
            }
        });

        Assert.Throws<InvalidOperationException>(() =>
            DeploymentOrchestrationService.ParseAndSortLayers(essenceJson, Guid.NewGuid()));
    }
}
