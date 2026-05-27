using System.Text.Json;
using Aura.Worker.Operations.Aws;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class CreateVpcHandlerTests
{
    private static CreateVpcHandler NewHandler() =>
        new(Mock.Of<ILogger<CreateVpcHandler>>());

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task ExecuteAsync_MissingVpcName_FailsFast()
    {
        var handler = NewHandler();
        var result = await handler.ExecuteAsync(
            "vpc-layer",
            Json("""{ "cidrBlock": "10.0.0.0/16" }"""),
            new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("vpcName", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_VpcNameWrongType_FailsFast()
    {
        var handler = NewHandler();
        var result = await handler.ExecuteAsync(
            "vpc-layer",
            Json("""{ "vpcName": 42 }"""),
            new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("vpcName", result.Output);
    }
}
