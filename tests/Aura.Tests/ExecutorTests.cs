using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class ExecutorTests
{
    [Fact]
    public async Task PowerShellExecutor_NoScriptPath_ReturnsFailed()
    {
        var logger = Mock.Of<ILogger<PowerShellExecutor>>();
        var executor = new PowerShellExecutor(logger);

        var layer = new DeploymentLayer
        {
            LayerName = "Test",
            ExecutorType = ExecutorType.PowerShell,
            ScriptPath = null,
            Parameters = "{}"
        };

        var result = await executor.ExecuteAsync(layer, "/tmp", new(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("No scriptPath", result.Output);
    }

    [Fact]
    public async Task PowerShellExecutor_MissingScript_ReturnsFailed()
    {
        var logger = Mock.Of<ILogger<PowerShellExecutor>>();
        var executor = new PowerShellExecutor(logger);

        var layer = new DeploymentLayer
        {
            LayerName = "Test",
            ExecutorType = ExecutorType.PowerShell,
            ScriptPath = "nonexistent.ps1",
            Parameters = "{}"
        };

        var result = await executor.ExecuteAsync(layer, "/tmp", new(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Output);
    }

    [Fact]
    public async Task PythonExecutor_NoScriptPath_ReturnsFailed()
    {
        var logger = Mock.Of<ILogger<PythonExecutor>>();
        var executor = new PythonExecutor(logger);

        var layer = new DeploymentLayer
        {
            LayerName = "Test",
            ExecutorType = ExecutorType.Python,
            ScriptPath = null,
            Parameters = "{}"
        };

        var result = await executor.ExecuteAsync(layer, "/tmp", new(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("No scriptPath", result.Output);
    }

    [Fact]
    public async Task PythonExecutor_MissingScript_ReturnsFailed()
    {
        var logger = Mock.Of<ILogger<PythonExecutor>>();
        var executor = new PythonExecutor(logger);

        var layer = new DeploymentLayer
        {
            LayerName = "Test",
            ExecutorType = ExecutorType.Python,
            ScriptPath = "nonexistent.py",
            Parameters = "{}"
        };

        var result = await executor.ExecuteAsync(layer, "/tmp", new(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Output);
    }
}
