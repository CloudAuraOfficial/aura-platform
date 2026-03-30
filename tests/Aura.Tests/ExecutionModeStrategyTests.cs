using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aura.Tests;

public class ExecutionModeStrategyTests
{
    private static ExecutionModeStrategy CreateStrategy(string mode)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EXECUTION_MODE"] = mode
            })
            .Build();
        return new ExecutionModeStrategy(config, NullLogger<ExecutionModeStrategy>.Instance);
    }

    private static (DeploymentRun Run, DeploymentLayer Layer) CreateRunAndLayer(
        ExecutorType executorType = ExecutorType.Operation,
        string? snapshotJson = null)
    {
        var run = new DeploymentRun
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Status = RunStatus.Running,
            SnapshotJson = snapshotJson ?? """{ "layers": {} }"""
        };
        var layer = new DeploymentLayer
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Run = run,
            LayerName = "TestLayer",
            ExecutorType = executorType,
            Status = LayerStatus.Pending
        };
        return (run, layer);
    }

    [Fact]
    public void InProcessMode_AlwaysReturnsInProcess()
    {
        var strategy = CreateStrategy("InProcess");
        var (run, layer) = CreateRunAndLayer();

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.InProcess, result);
    }

    [Fact]
    public void InProcessMode_IgnoresBaseLoad()
    {
        var strategy = CreateStrategy("InProcess");
        var snapshot = """
        {
            "baseEssence": { "baseLoad": "EmissionLoadACI", "cloudProvider": "Azure" },
            "layers": {}
        }
        """;
        var (run, layer) = CreateRunAndLayer(snapshotJson: snapshot);

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.InProcess, result);
    }

    [Fact]
    public void EmissionLoadMode_OperationLayer_ReturnsEmissionLoad()
    {
        var strategy = CreateStrategy("EmissionLoad");
        var (run, layer) = CreateRunAndLayer(ExecutorType.Operation);

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.EmissionLoadContainer, result);
    }

    [Fact]
    public void EmissionLoadMode_ScriptLayer_ReturnsInProcess()
    {
        var strategy = CreateStrategy("EmissionLoad");
        var (run, layer) = CreateRunAndLayer(ExecutorType.PowerShell);

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.InProcess, result);
    }

    [Fact]
    public void AutoMode_WithBaseLoad_OperationLayer_ReturnsEmissionLoad()
    {
        var strategy = CreateStrategy("Auto");
        var snapshot = """
        {
            "baseEssence": { "baseLoad": "EmissionLoadACI", "cloudProvider": "Azure" },
            "layers": {}
        }
        """;
        var (run, layer) = CreateRunAndLayer(ExecutorType.Operation, snapshot);

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.EmissionLoadContainer, result);
    }

    [Fact]
    public void AutoMode_WithoutBaseLoad_ReturnsInProcess()
    {
        var strategy = CreateStrategy("Auto");
        var snapshot = """{ "layers": {} }""";
        var (run, layer) = CreateRunAndLayer(ExecutorType.Operation, snapshot);

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.InProcess, result);
    }

    [Fact]
    public void AutoMode_ScriptLayer_WithBaseLoad_ReturnsInProcess()
    {
        var strategy = CreateStrategy("Auto");
        var snapshot = """
        {
            "baseEssence": { "baseLoad": "EmissionLoadACI" },
            "layers": {}
        }
        """;
        var (run, layer) = CreateRunAndLayer(ExecutorType.Python, snapshot);

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.InProcess, result);
    }

    [Fact]
    public void ExplicitEmissionLoadExecutorType_AlwaysReturnsContainer()
    {
        var strategy = CreateStrategy("InProcess");
        var (run, layer) = CreateRunAndLayer(ExecutorType.EmissionLoad);

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.EmissionLoadContainer, result);
    }

    [Fact]
    public void DefaultMode_UnknownValue_ReturnsInProcess()
    {
        var strategy = CreateStrategy("SomeGarbage");
        var (run, layer) = CreateRunAndLayer();

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.InProcess, result);
    }

    [Fact]
    public void AutoMode_InvalidJson_ReturnsInProcess()
    {
        var strategy = CreateStrategy("Auto");
        var (run, layer) = CreateRunAndLayer(ExecutorType.Operation, "not-json");

        var result = strategy.Resolve(run, layer);

        Assert.Equal(ExecutionMode.InProcess, result);
    }
}
