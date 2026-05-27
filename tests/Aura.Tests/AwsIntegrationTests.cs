using System.Text.Json;
using Aura.Worker.Operations.Aws;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Aura.Tests;

/// <summary>
/// Live AWS integration test for the Epic 1 handler set. SKIPPED unless the
/// AWS_INTEGRATION=true env var is set AND AWS credentials are present in
/// env (AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY / AWS_REGION).
///
/// The class name contains "Integration" so the default test filter
/// (`FullyQualifiedName!~Integration`) already excludes it from CI / local
/// runs. The env-var check inside each test is belt-and-braces: even if
/// someone runs the integration suite, nothing fires against AWS until the
/// gate flag is flipped.
///
/// To run locally:
///   AWS_INTEGRATION=true \
///   AWS_ACCESS_KEY_ID=... AWS_SECRET_ACCESS_KEY=... AWS_REGION=us-east-1 \
///   dotnet test --filter "FullyQualifiedName~AwsIntegration"
/// </summary>
public class AwsIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public AwsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool Gated() =>
        string.Equals(Environment.GetEnvironmentVariable("AWS_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> EnvCreds() => new()
    {
        ["AWS_ACCESS_KEY_ID"]     = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? "",
        ["AWS_SECRET_ACCESS_KEY"] = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? "",
        ["AWS_REGION"]            = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1",
    };

    [Fact]
    public async Task CreateVpc_ThenDeleteVpc_RoundTrips()
    {
        if (!Gated())
        {
            _output.WriteLine("AWS_INTEGRATION not set — skipping.");
            return;
        }

        var vpcName = $"aura-it-vpc-{Guid.NewGuid():N}".Substring(0, 32);
        var envVars = EnvCreds();
        var create = new CreateVpcHandler(Mock.Of<ILogger<CreateVpcHandler>>());
        var delete = new DeleteVpcHandler(Mock.Of<ILogger<DeleteVpcHandler>>());

        var createParams = JsonDocument.Parse($$"""
            { "vpcName": "{{vpcName}}", "cidrBlock": "10.99.0.0/16", "subnetCidr": "10.99.0.0/24" }
            """).RootElement;
        var createResult = await create.ExecuteAsync("create-vpc", createParams, envVars);
        _output.WriteLine($"CREATE: {createResult.Output}");
        Assert.True(createResult.Success, createResult.Output);

        try
        {
            var deleteParams = JsonDocument.Parse($$"""{ "vpcName": "{{vpcName}}" }""").RootElement;
            var deleteResult = await delete.ExecuteAsync("delete-vpc", deleteParams, envVars);
            _output.WriteLine($"DELETE: {deleteResult.Output}");
            Assert.True(deleteResult.Success, deleteResult.Output);
        }
        catch
        {
            // Last-ditch cleanup so a failed assert doesn't leak VPCs.
            try
            {
                var deleteParams = JsonDocument.Parse($$"""{ "vpcName": "{{vpcName}}" }""").RootElement;
                await delete.ExecuteAsync("delete-vpc-cleanup", deleteParams, envVars);
            }
            catch { }
            throw;
        }
    }
}
