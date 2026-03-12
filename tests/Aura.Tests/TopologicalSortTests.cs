using Aura.Core.Enums;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

public class TopologicalSortTests
{
    [Fact]
    public void LinearChain_SortsInOrder()
    {
        var defs = new Dictionary<string, DeploymentOrchestrationService.LayerDefinition>
        {
            ["A"] = new("A", ExecutorType.PowerShell, "{}", null, []),
            ["B"] = new("B", ExecutorType.Python, "{}", null, ["A"]),
            ["C"] = new("C", ExecutorType.CSharpSdk, "{}", null, ["B"])
        };

        var sorted = DeploymentOrchestrationService.TopologicalSort(defs);

        Assert.Equal(3, sorted.Count);
        Assert.Equal("A", sorted[0].Name);
        Assert.Equal("B", sorted[1].Name);
        Assert.Equal("C", sorted[2].Name);
    }

    [Fact]
    public void DiamondDependency_SortsCorrectly()
    {
        var defs = new Dictionary<string, DeploymentOrchestrationService.LayerDefinition>
        {
            ["A"] = new("A", ExecutorType.PowerShell, "{}", null, []),
            ["B"] = new("B", ExecutorType.PowerShell, "{}", null, ["A"]),
            ["C"] = new("C", ExecutorType.PowerShell, "{}", null, ["A"]),
            ["D"] = new("D", ExecutorType.PowerShell, "{}", null, ["B", "C"])
        };

        var sorted = DeploymentOrchestrationService.TopologicalSort(defs);

        Assert.Equal(4, sorted.Count);
        var indexOf = sorted.Select((s, i) => (s.Name, i)).ToDictionary(x => x.Name, x => x.i);
        Assert.True(indexOf["A"] < indexOf["B"]);
        Assert.True(indexOf["A"] < indexOf["C"]);
        Assert.True(indexOf["B"] < indexOf["D"]);
        Assert.True(indexOf["C"] < indexOf["D"]);
    }

    [Fact]
    public void NoDependencies_ReturnsAlphabetical()
    {
        var defs = new Dictionary<string, DeploymentOrchestrationService.LayerDefinition>
        {
            ["C"] = new("C", ExecutorType.PowerShell, "{}", null, []),
            ["A"] = new("A", ExecutorType.PowerShell, "{}", null, []),
            ["B"] = new("B", ExecutorType.PowerShell, "{}", null, [])
        };

        var sorted = DeploymentOrchestrationService.TopologicalSort(defs);

        Assert.Equal(3, sorted.Count);
        Assert.Equal("A", sorted[0].Name);
        Assert.Equal("B", sorted[1].Name);
        Assert.Equal("C", sorted[2].Name);
    }

    [Fact]
    public void CyclicDependency_Throws()
    {
        var defs = new Dictionary<string, DeploymentOrchestrationService.LayerDefinition>
        {
            ["A"] = new("A", ExecutorType.PowerShell, "{}", null, ["B"]),
            ["B"] = new("B", ExecutorType.PowerShell, "{}", null, ["A"])
        };

        Assert.Throws<InvalidOperationException>(
            () => DeploymentOrchestrationService.TopologicalSort(defs));
    }

    [Fact]
    public void SingleLayer_ReturnsOne()
    {
        var defs = new Dictionary<string, DeploymentOrchestrationService.LayerDefinition>
        {
            ["Only"] = new("Only", ExecutorType.Python, "{}", "run.py", [])
        };

        var sorted = DeploymentOrchestrationService.TopologicalSort(defs);
        Assert.Single(sorted);
        Assert.Equal("Only", sorted[0].Name);
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        var sorted = DeploymentOrchestrationService.TopologicalSort(
            new Dictionary<string, DeploymentOrchestrationService.LayerDefinition>());
        Assert.Empty(sorted);
    }
}
