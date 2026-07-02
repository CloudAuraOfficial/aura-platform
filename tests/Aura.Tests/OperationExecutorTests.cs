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

    // Hangs until its token is cancelled — stands in for a stuck cloud SDK call.
    private class HangingHandler : IOperationHandler
    {
        public async Task<LayerExecutionResult> ExecuteAsync(
            string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new LayerExecutionResult(true, "should never reach here");
        }
    }

    // #16: a hung operation must fail fast at the client-side ceiling, not block forever.
    [Fact]
    public async Task ExecuteAsync_Times_Out_A_Hung_Operation()
    {
        var registry = new OperationRegistry();
        registry.Register<HangingHandler>("CreateVNet");

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(HangingHandler))).Returns(new HangingHandler());

        var executor = new OperationExecutor(
            sp.Object, registry, Mock.Of<ILogger<OperationExecutor>>(),
            operationTimeout: TimeSpan.FromMilliseconds(200));

        var layer = new DeploymentLayer
        {
            LayerName = "TestLayer",
            ExecutorType = ExecutorType.Operation,
            OperationType = "CreateVNet",
            Parameters = "{}"
        };

        var result = await executor.ExecuteAsync(layer, "/tmp", new(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Output);
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
