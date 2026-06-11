using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Infrastructure.Services;
using Aura.Worker.Services;
using Xunit;

namespace Aura.Tests;

/// <summary>
/// Tests for #13: runPolicy parsing and the executor's finally-semantics
/// skip decision (always-run teardown layers).
/// </summary>
public class RunPolicyTests
{
    // ───────────────────────── Parsing ─────────────────────────

    [Fact]
    public void ParseAndSortLayers_RunPolicyAbsent_DefaultsToOnSuccess()
    {
        var json = """
            {
              "layers": {
                "L1": { "isEnabled": true, "operationType": "CreateVpc", "parameters": {} }
              }
            }
            """;
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());
        Assert.Single(layers);
        Assert.Equal(RunPolicy.OnSuccess, layers[0].RunPolicy);
    }

    [Theory]
    [InlineData("always", RunPolicy.Always)]
    [InlineData("Always", RunPolicy.Always)]
    [InlineData("ALWAYS", RunPolicy.Always)]
    [InlineData("onSuccess", RunPolicy.OnSuccess)]
    [InlineData("onsuccess", RunPolicy.OnSuccess)]
    public void ParseAndSortLayers_RunPolicy_ParsedCaseInsensitively(string value, RunPolicy expected)
    {
        var json = $$"""
            {
              "layers": {
                "L1": {
                  "isEnabled": true, "operationType": "DeleteVpc",
                  "parameters": {}, "runPolicy": "{{value}}"
                }
              }
            }
            """;
        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());
        Assert.Single(layers);
        Assert.Equal(expected, layers[0].RunPolicy);
    }

    [Fact]
    public void ParseAndSortLayers_UnknownRunPolicy_ThrowsAtRunCreation()
    {
        // Strict parse: runPolicy is a safety property. A typo silently
        // falling back to fail-stop would quietly reintroduce orphaning.
        var json = """
            {
              "layers": {
                "L1": {
                  "isEnabled": true, "operationType": "DeleteVpc",
                  "parameters": {}, "runPolicy": "alway"
                }
              }
            }
            """;
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid()));
        Assert.Contains("runPolicy", ex.Message);
        Assert.Contains("L1", ex.Message);
    }

    [Fact]
    public void ParseAndSortLayers_NonStringRunPolicy_Throws()
    {
        var json = """
            {
              "layers": {
                "L1": {
                  "isEnabled": true, "operationType": "DeleteVpc",
                  "parameters": {}, "runPolicy": 1
                }
              }
            }
            """;
        Assert.Throws<InvalidOperationException>(
            () => DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid()));
    }

    // ───────────────────── Executor skip decision ─────────────────────

    private static DeploymentLayer Layer(
        string name, RunPolicy policy = RunPolicy.OnSuccess, params string[] dependsOn) => new()
    {
        LayerName = name,
        RunPolicy = policy,
        DependsOn = System.Text.Json.JsonSerializer.Serialize(dependsOn.ToList())
    };

    [Fact]
    public void OnSuccessLayer_NoFailure_Runs()
    {
        var reason = RunWorkerService.ComputeSkipReason(
            Layer("Create"), failed: false, alwaysLayers: [], outcomes: []);
        Assert.Null(reason);
    }

    [Fact]
    public void OnSuccessLayer_AfterFailure_Skips()
    {
        var reason = RunWorkerService.ComputeSkipReason(
            Layer("Create2"), failed: true, alwaysLayers: [], outcomes: []);
        Assert.Equal("previous layer failed", reason);
    }

    [Fact]
    public void AlwaysLayer_AfterUnrelatedFailure_Runs()
    {
        // The 2026-05-28 incident: health check failed → every teardown
        // skipped. Always layers must survive the global fail-stop.
        var teardown = Layer("GcsTeardown", RunPolicy.Always, "HealthCheck");
        var reason = RunWorkerService.ComputeSkipReason(
            teardown, failed: true,
            alwaysLayers: ["GcsTeardown"],
            outcomes: new() { ["HealthCheck"] = LayerStatus.Failed });
        Assert.Null(reason); // HealthCheck is not an Always layer → ordering-only
    }

    [Fact]
    public void AlwaysLayer_FailedAlwaysDependency_Skips()
    {
        // The fc35010f incident inverse: VpcTeardown behind a failed
        // Ec2Teardown would hit DependencyViolation — skip it.
        var vpcTeardown = Layer("VpcTeardown", RunPolicy.Always, "Ec2Teardown");
        var reason = RunWorkerService.ComputeSkipReason(
            vpcTeardown, failed: true,
            alwaysLayers: ["VpcTeardown", "Ec2Teardown"],
            outcomes: new() { ["Ec2Teardown"] = LayerStatus.Failed });
        Assert.NotNull(reason);
        Assert.Contains("Ec2Teardown", reason);
    }

    [Fact]
    public void AlwaysLayer_SkippedAlwaysDependency_CascadesSkip()
    {
        var rgTeardown = Layer("RgTeardown", RunPolicy.Always, "VnetTeardown");
        var reason = RunWorkerService.ComputeSkipReason(
            rgTeardown, failed: true,
            alwaysLayers: ["RgTeardown", "VnetTeardown"],
            outcomes: new() { ["VnetTeardown"] = LayerStatus.Skipped });
        Assert.NotNull(reason);
        Assert.Contains("VnetTeardown", reason);
    }

    [Fact]
    public void AlwaysLayer_SucceededAlwaysDependency_Runs()
    {
        var vpcTeardown = Layer("VpcTeardown", RunPolicy.Always, "Ec2Teardown");
        var reason = RunWorkerService.ComputeSkipReason(
            vpcTeardown, failed: true,
            alwaysLayers: ["VpcTeardown", "Ec2Teardown"],
            outcomes: new() { ["Ec2Teardown"] = LayerStatus.Succeeded });
        Assert.Null(reason);
    }

    [Fact]
    public void AlwaysLayer_SiblingCleanupFailure_DoesNotSkip()
    {
        // The fc35010f incident: AwsVpcTeardown failing must not orphan the
        // Azure side. Sibling cleanups on other clouds share no dependsOn
        // edge, so each proceeds independently.
        var azureTeardown = Layer("AzureRgTeardown", RunPolicy.Always, "HealthCheck");
        var reason = RunWorkerService.ComputeSkipReason(
            azureTeardown, failed: true,
            alwaysLayers: ["AzureRgTeardown", "AwsVpcTeardown"],
            outcomes: new()
            {
                ["HealthCheck"] = LayerStatus.Succeeded,
                ["AwsVpcTeardown"] = LayerStatus.Failed
            });
        Assert.Null(reason);
    }

    // ─────────────── Full-scenario matrix (May-28 + fc35010f) ───────────────

    [Fact]
    public void May28Scenario_HealthCheckFails_AllTeardownsStillRun()
    {
        // Reproduces the original orphaning incident shape with runPolicy
        // applied: creates succeed, health check fails, every teardown whose
        // Always-ancestors succeeded must run.
        var alwaysLayers = new HashSet<string>
        {
            "GcsTeardown", "Ec2Teardown", "VpcTeardown", "VnetTeardown", "RgTeardown"
        };
        var outcomes = new Dictionary<string, LayerStatus>
        {
            ["Rg"] = LayerStatus.Succeeded,
            ["Vnet"] = LayerStatus.Succeeded,
            ["Vpc"] = LayerStatus.Succeeded,
            ["Ec2"] = LayerStatus.Succeeded,
            ["Gcs"] = LayerStatus.Succeeded,
            ["HealthCheck"] = LayerStatus.Failed
        };

        // Walk teardowns in topo order, recording outcomes as the loop would.
        var teardowns = new[]
        {
            Layer("GcsTeardown", RunPolicy.Always, "HealthCheck"),
            Layer("Ec2Teardown", RunPolicy.Always, "HealthCheck"),
            Layer("VpcTeardown", RunPolicy.Always, "Ec2Teardown"),
            Layer("VnetTeardown", RunPolicy.Always, "HealthCheck"),
            Layer("RgTeardown", RunPolicy.Always, "VnetTeardown")
        };

        foreach (var layer in teardowns)
        {
            var reason = RunWorkerService.ComputeSkipReason(layer, failed: true, alwaysLayers, outcomes);
            Assert.Null(reason);
            outcomes[layer.LayerName] = LayerStatus.Succeeded; // simulate success
        }
    }
}
