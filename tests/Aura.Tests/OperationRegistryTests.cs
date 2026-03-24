using System.Text.Json;
using Aura.Worker.Executors;
using Aura.Worker.Operations;
using Moq;
using Xunit;

namespace Aura.Tests;

public class OperationRegistryTests
{
    private class TestHandler : IOperationHandler
    {
        public Task<LayerExecutionResult> ExecuteAsync(
            string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct)
            => Task.FromResult(new LayerExecutionResult(true, "test"));
    }

    private class AnotherTestHandler : IOperationHandler
    {
        public Task<LayerExecutionResult> ExecuteAsync(
            string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct)
            => Task.FromResult(new LayerExecutionResult(true, "another"));
    }

    [Fact]
    public void Register_And_Resolve_Returns_Correct_Handler()
    {
        var registry = new OperationRegistry();
        registry.Register<TestHandler>("CreateVM");

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(TestHandler))).Returns(new TestHandler());

        var handler = registry.Resolve(sp.Object, "CreateVM");

        Assert.NotNull(handler);
        Assert.IsType<TestHandler>(handler);
    }

    [Fact]
    public void Resolve_Unknown_Type_Throws_InvalidOperationException()
    {
        var registry = new OperationRegistry();
        var sp = new Mock<IServiceProvider>();

        Assert.Throws<InvalidOperationException>(() =>
            registry.Resolve(sp.Object, "NonExistentType"));
    }

    [Fact]
    public void Register_Is_Case_Insensitive()
    {
        var registry = new OperationRegistry();
        registry.Register<TestHandler>("CreateVM");

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(TestHandler))).Returns(new TestHandler());

        var handler = registry.Resolve(sp.Object, "createvm");

        Assert.NotNull(handler);
        Assert.IsType<TestHandler>(handler);
    }

    [Fact]
    public void Register_Multiple_Types()
    {
        var registry = new OperationRegistry();
        registry.Register<TestHandler>("CreateVM");
        registry.Register<AnotherTestHandler>("DeleteVM");

        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(TestHandler))).Returns(new TestHandler());
        sp.Setup(s => s.GetService(typeof(AnotherTestHandler))).Returns(new AnotherTestHandler());

        var handler1 = registry.Resolve(sp.Object, "CreateVM");
        var handler2 = registry.Resolve(sp.Object, "DeleteVM");

        Assert.IsType<TestHandler>(handler1);
        Assert.IsType<AnotherTestHandler>(handler2);
    }
}
