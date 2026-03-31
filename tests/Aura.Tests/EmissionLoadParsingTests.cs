using System.Text.Json;
using Aura.Core.Enums;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

public class EmissionLoadParsingTests
{
    [Fact]
    public void ParseExecutorType_EmissionLoad_ReturnsCorrectType()
    {
        var essenceJson = JsonSerializer.Serialize(new
        {
            layers = new Dictionary<string, object>
            {
                ["ContainerLayer"] = new
                {
                    isEnabled = true,
                    executorType = "emissionload",
                    operationType = "CreateContainerGroup",
                    parameters = new { containerGroupName = "test" }
                }
            }
        });

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(essenceJson, Guid.NewGuid());

        Assert.Single(layers);
        Assert.Equal(ExecutorType.EmissionLoad, layers[0].ExecutorType);
        Assert.Equal("CreateContainerGroup", layers[0].OperationType);
    }

    [Fact]
    public void ParseAndSortLayers_MixedExecutorTypes_PreservesAll()
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
                    operationType = "CreateResourceGroup",
                    parameters = new { resourceGroupName = "test-rg" },
                    dependsOn = new[] { "ScriptLayer" }
                },
                ["EmissionLoadLayer"] = new
                {
                    isEnabled = true,
                    executorType = "emissionload",
                    operationType = "CreateVM",
                    parameters = new { vmName = "test-vm" },
                    dependsOn = new[] { "OperationLayer" }
                }
            }
        });

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(essenceJson, Guid.NewGuid());

        Assert.Equal(3, layers.Count);
        Assert.Equal(ExecutorType.PowerShell, layers[0].ExecutorType);
        Assert.Equal(ExecutorType.Operation, layers[1].ExecutorType);
        Assert.Equal(ExecutorType.EmissionLoad, layers[2].ExecutorType);
    }

    [Fact]
    public void ParseAndSortLayers_EmissionLoad_PreservesOperationType()
    {
        var essenceJson = JsonSerializer.Serialize(new
        {
            layers = new Dictionary<string, object>
            {
                ["DeployACI"] = new
                {
                    isEnabled = true,
                    executorType = "emissionload",
                    operationType = "CreateContainerGroup",
                    parameters = new
                    {
                        containerGroupName = "aura-api",
                        resourceGroup = "cloudaura-rg"
                    }
                }
            }
        });

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(essenceJson, Guid.NewGuid());

        var layer = Assert.Single(layers);
        Assert.Equal("CreateContainerGroup", layer.OperationType);
        Assert.Equal(ExecutorType.EmissionLoad, layer.ExecutorType);
        Assert.Contains("containerGroupName", layer.Parameters);
    }

    /// <summary>
    /// Discovers all Essencefiles under Essences/ and validates structural correctness.
    /// Adding a new customer folder with an Essencefile automatically includes it.
    /// </summary>
    public static IEnumerable<object[]> AllEssencefiles()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var essencesDir = Path.Combine(repoRoot, "Essences");
        if (!Directory.Exists(essencesDir))
            yield break;

        foreach (var file in Directory.GetFiles(essencesDir, "Essencefile.json", SearchOption.AllDirectories))
        {
            // Use relative path from Essences/ as the display name
            var relativePath = Path.GetRelativePath(essencesDir, file);
            yield return new object[] { file, relativePath };
        }
    }

    [Theory]
    [MemberData(nameof(AllEssencefiles))]
    public void Essencefile_ParsesCorrectly(string filePath, string displayName)
    {
        var json = File.ReadAllText(filePath);

        // 1. Parsing succeeds
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());

        // 2. Count matches enabled layers in source JSON
        var expectedCount = CountEnabledLayers(json);
        Assert.Equal(expectedCount, layers.Count);

        // 3. Every layer has a non-empty name
        Assert.All(layers, l => Assert.False(
            string.IsNullOrWhiteSpace(l.LayerName),
            $"Layer at SortOrder {l.SortOrder} has empty name in {displayName}"));

        // 4. Every layer has a valid ExecutorType
        Assert.All(layers, l => Assert.True(
            Enum.IsDefined(typeof(ExecutorType), l.ExecutorType),
            $"Layer '{l.LayerName}' has undefined ExecutorType {l.ExecutorType} in {displayName}"));

        // 5. Sort order is sequential starting from 0
        for (var i = 0; i < layers.Count; i++)
            Assert.Equal(i, layers[i].SortOrder);

        // 6. Dependencies reference layers that exist in the parsed result
        var layerNames = layers.Select(l => l.LayerName).ToHashSet();
        foreach (var layer in layers)
        {
            var deps = JsonSerializer.Deserialize<List<string>>(layer.DependsOn) ?? [];
            foreach (var dep in deps)
                Assert.Contains(dep, layerNames);
        }

        // 7. All layers start in Pending status
        Assert.All(layers, l => Assert.Equal(LayerStatus.Pending, l.Status));

        // 8. Parameters is valid JSON
        Assert.All(layers, l =>
        {
            var ex = Record.Exception(() => JsonDocument.Parse(l.Parameters));
            Assert.Null(ex);
        });
    }

    private static int CountEnabledLayers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("layers", out var layers))
            return 0;

        var count = 0;
        foreach (var prop in layers.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("isEnabled", out var enabled) && enabled.GetBoolean())
                count++;
        }
        return count;
    }
}
