using System.Text.Json;
using Aura.Worker.Operations.Azure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

public class CreateVirtualNetworkHandlerTests
{
    private static CreateVirtualNetworkHandler NewHandler() =>
        new(Mock.Of<ILogger<CreateVirtualNetworkHandler>>());

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task ExecuteAsync_MissingVnetName_FailsFast()
    {
        var result = await NewHandler().ExecuteAsync(
            "vnet-layer",
            Json("""{ "resourceGroupName": "rg", "location": "eastus" }"""),
            new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("vnetName", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MissingResourceGroup_FailsFast()
    {
        var result = await NewHandler().ExecuteAsync(
            "vnet-layer",
            Json("""{ "vnetName": "v", "location": "eastus" }"""),
            new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("resourceGroupName", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MissingLocation_FailsFast()
    {
        var result = await NewHandler().ExecuteAsync(
            "vnet-layer",
            Json("""{ "vnetName": "v", "resourceGroupName": "rg" }"""),
            new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("location", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedSubnet_FailsFast()
    {
        var result = await NewHandler().ExecuteAsync(
            "vnet-layer",
            Json("""
            {
              "vnetName": "v",
              "resourceGroupName": "rg",
              "location": "eastus",
              "subnets": [ { "name": "web" } ]
            }
            """),
            new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("addressPrefix", result.Output);
    }
}

public class DeleteVirtualNetworkHandlerTests
{
    private static DeleteVirtualNetworkHandler NewHandler() =>
        new(Mock.Of<ILogger<DeleteVirtualNetworkHandler>>());

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task ExecuteAsync_MissingVnetName_FailsFast()
    {
        var result = await NewHandler().ExecuteAsync(
            "vnet-teardown",
            Json("""{ "resourceGroupName": "rg" }"""),
            new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("vnetName", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_MissingResourceGroup_FailsFast()
    {
        var result = await NewHandler().ExecuteAsync(
            "vnet-teardown",
            Json("""{ "vnetName": "v" }"""),
            new Dictionary<string, string>());

        Assert.False(result.Success);
        Assert.Contains("resourceGroupName", result.Output);
    }
}
