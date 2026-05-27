using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

/// <summary>
/// Tests for Epic 3's per-layer cloudAccountId override parsing.
/// </summary>
public class PerLayerCloudAccountIdTests
{
    private const string AcctA = "11111111-1111-1111-1111-111111111111";
    private const string AcctB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void ParseAndSortLayers_LayerWithCloudAccountId_PopulatesProperty()
    {
        var json = $$"""
            {
              "layers": {
                "L1": {
                  "isEnabled": true, "operationType": "CreateVpc",
                  "parameters": {}, "cloudAccountId": "{{AcctA}}"
                }
              }
            }
            """;
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());
        Assert.Single(layers);
        Assert.Equal(Guid.Parse(AcctA), layers[0].CloudAccountId);
    }

    [Fact]
    public void ParseAndSortLayers_LayerWithoutCloudAccountId_LeavesNull()
    {
        var json = """
            {
              "layers": {
                "L1": {
                  "isEnabled": true, "operationType": "CreateVpc",
                  "parameters": {}
                }
              }
            }
            """;
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());
        Assert.Single(layers);
        Assert.Null(layers[0].CloudAccountId);
    }

    [Fact]
    public void ParseAndSortLayers_MultipleLayers_IndependentAccountIds()
    {
        var json = $$"""
            {
              "layers": {
                "L1": {
                  "isEnabled": true, "operationType": "CreateVpc",
                  "parameters": {}, "cloudAccountId": "{{AcctA}}"
                },
                "L2": {
                  "isEnabled": true, "operationType": "CreateGcsBucket",
                  "parameters": {}, "cloudAccountId": "{{AcctB}}",
                  "dependsOn": ["L1"]
                },
                "L3": {
                  "isEnabled": true, "operationType": "HttpHealthCheck",
                  "parameters": {}, "dependsOn": ["L2"]
                }
              }
            }
            """;
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());
        Assert.Equal(3, layers.Count);

        var byName = layers.ToDictionary(l => l.LayerName);
        Assert.Equal(Guid.Parse(AcctA), byName["L1"].CloudAccountId);
        Assert.Equal(Guid.Parse(AcctB), byName["L2"].CloudAccountId);
        Assert.Null(byName["L3"].CloudAccountId);
    }

    [Fact]
    public void ParseAndSortLayers_InvalidGuidString_TreatedAsAbsent()
    {
        // Silent fallback rather than parse failure — protects existing
        // Essences from breaking on a malformed override.
        var json = """
            {
              "layers": {
                "L1": {
                  "isEnabled": true, "operationType": "CreateVpc",
                  "parameters": {}, "cloudAccountId": "not-a-guid"
                }
              }
            }
            """;
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());
        Assert.Single(layers);
        Assert.Null(layers[0].CloudAccountId);
    }

    [Fact]
    public void ParseAndSortLayers_NonStringCloudAccountId_TreatedAsAbsent()
    {
        var json = """
            {
              "layers": {
                "L1": {
                  "isEnabled": true, "operationType": "CreateVpc",
                  "parameters": {}, "cloudAccountId": 42
                }
              }
            }
            """;
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());
        Assert.Single(layers);
        Assert.Null(layers[0].CloudAccountId);
    }
}
