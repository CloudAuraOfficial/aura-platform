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

    [Fact]
    public void Essencefile_Aura_AciDeploy_StillParsesCorrectly()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "Essences", "Aura", "aci-deploy", "Essencefile.json");
        var json = File.ReadAllText(path);

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());

        // 9 enabled layers (2 disabled: StopContainerGroup, DeleteContainerGroup)
        Assert.Equal(9, layers.Count);
        Assert.All(layers, l => Assert.Equal(ExecutorType.Operation, l.ExecutorType));
    }

    [Fact]
    public void Essencefile_Aura_VmDeploy_StillParsesCorrectly()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "Essences", "Aura", "vm-deploy", "Essencefile.json");
        var json = File.ReadAllText(path);

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());

        // 2 enabled layers (3 disabled: HealthCheck, StopVM, DeleteVM)
        Assert.Equal(2, layers.Count);
        Assert.Equal("ResourceGroup", layers[0].LayerName);
        Assert.Equal("CreateVM", layers[1].LayerName);
    }
}
