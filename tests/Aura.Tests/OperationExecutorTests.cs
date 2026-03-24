using System.Text.Json;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Worker.Executors;
using Aura.Worker.Operations;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class OperationExecutorTests
{
    private class TestHandler : IOperationHandler
    {
        public bool WasCalled { get; private set; }

        public Task<LayerExecutionResult> ExecuteAsync(
            string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(new LayerExecutionResult(true, "handler executed"));
        }
    }

    [Fact]
    public async Task ExecuteAsync_Dispatches_To_Correct_Handler()
    {
        var handler = new TestHandler();
        var registry = new OperationRegistry();
        registry.Register<TestHandler>("CreateVM");

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(TestHandler))).Returns(handler);

        var logger = Mock.Of<ILogger<OperationExecutor>>();
        var executor = new OperationExecutor(sp.Object, registry, logger);

        var layer = new DeploymentLayer
        {
            LayerName = "TestLayer",
            ExecutorType = ExecutorType.Operation,
            OperationType = "CreateVM",
            Parameters = """{"vmName": "test-vm"}"""
        };

        var result = await executor.ExecuteAsync(layer, "/tmp", new(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(handler.WasCalled);
    }

    [Fact]
    public async Task ExecuteAsync_No_OperationType_Returns_Failure()
    {
        var registry = new OperationRegistry();
        var sp = new Mock<IServiceProvider>();
        var logger = Mock.Of<ILogger<OperationExecutor>>();
        var executor = new OperationExecutor(sp.Object, registry, logger);

        var layer = new DeploymentLayer
        {
            LayerName = "TestLayer",
            ExecutorType = ExecutorType.Operation,
            OperationType = null,
            Parameters = """{"vmName": "test-vm"}"""
        };

        var result = await executor.ExecuteAsync(layer, "/tmp", new(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No operationType", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_Unknown_OperationType_Returns_Failure()
    {
        var registry = new OperationRegistry();
        var sp = new Mock<IServiceProvider>();
        var logger = Mock.Of<ILogger<OperationExecutor>>();
        var executor = new OperationExecutor(sp.Object, registry, logger);

        var layer = new DeploymentLayer
        {
            LayerName = "TestLayer",
            ExecutorType = ExecutorType.Operation,
            OperationType = "NonExistentOperation",
            Parameters = """{"key": "value"}"""
        };

        var result = await executor.ExecuteAsync(layer, "/tmp", new(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No handler registered", result.Output);
    }
}
