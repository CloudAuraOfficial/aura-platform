using System.Text.Json;
using Aura.Worker.Operations.Gcp;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Aura.Tests;

/// <summary>
/// Live GCP integration test. SKIPPED unless GCP_INTEGRATION=true AND
/// GCP_SERVICE_ACCOUNT_JSON + GCP_PROJECT_ID are set.
///
/// Class name contains "Integration" so the default test filter
/// (`FullyQualifiedName!~Integration`) already excludes it.
///
/// To run locally:
///   GCP_INTEGRATION=true \
///   GCP_SERVICE_ACCOUNT_JSON='{"type":"service_account",...}' \
///   GCP_PROJECT_ID=my-project \
///   dotnet test --filter "FullyQualifiedName~GcpIntegration"
/// </summary>
public class GcpIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public GcpIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool Gated() =>
        string.Equals(Environment.GetEnvironmentVariable("GCP_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> EnvCreds() => new()
    {
        ["GCP_SERVICE_ACCOUNT_JSON"] = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_JSON") ?? "",
        ["GCP_PROJECT_ID"]           = Environment.GetEnvironmentVariable("GCP_PROJECT_ID") ?? "",
    };

    [Fact]
    public async Task CreateNetwork_ThenDeleteNetwork_RoundTrips()
    {
        if (!Gated())
        {
            _output.WriteLine("GCP_INTEGRATION not set — skipping.");
            return;
        }

        var netName = $"aura-it-{Guid.NewGuid():N}".Substring(0, 24);
        var envVars = EnvCreds();
        var create = new CreateNetworkHandler(Mock.Of<ILogger<CreateNetworkHandler>>());
        var delete = new DeleteNetworkHandler(Mock.Of<ILogger<DeleteNetworkHandler>>());

        var createParams = JsonDocument.Parse($$"""
            { "networkName": "{{netName}}", "subnetCidr": "10.77.0.0/24", "region": "us-central1" }
            """).RootElement;
        var createResult = await create.ExecuteAsync("create-network", createParams, envVars);
        _output.WriteLine($"CREATE: {createResult.Output}");
        Assert.True(createResult.Success, createResult.Output);

        try
        {
            var deleteParams = JsonDocument.Parse($$"""{ "networkName": "{{netName}}" }""").RootElement;
            var deleteResult = await delete.ExecuteAsync("delete-network", deleteParams, envVars);
            _output.WriteLine($"DELETE: {deleteResult.Output}");
            Assert.True(deleteResult.Success, deleteResult.Output);
        }
        catch
        {
            try
            {
                var deleteParams = JsonDocument.Parse($$"""{ "networkName": "{{netName}}" }""").RootElement;
                await delete.ExecuteAsync("delete-network-cleanup", deleteParams, envVars);
            }
            catch { }
            throw;
        }
    }
}
