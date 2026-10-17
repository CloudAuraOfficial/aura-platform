using System.Text.Json;
using Aura.Worker.Executors;
using Aura.Worker.Operations;
using Aura.Worker.Operations.Aws;
using Aura.Worker.Operations.Azure;
using Aura.Worker.Operations.Common;
using Aura.Worker.Operations.Gcp;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

/// <summary>
/// #53: every covered create handler rejects a bad expiresInMinutes before any SDK client is built or called.
/// These run with no credentials and no network, so they are safe to run offline.
/// </summary>
public class ExpiryTagHandlerValidationTests
{
    private static readonly Dictionary<string, string> RunEnv = new()
    {
        [ResourceTags.RunIdEnvVar] = Guid.NewGuid().ToString(),
        // GCP handlers resolve the project id before validating; supply one so the failure under test is the tag check.
        [GcpClientFactory.ProjectIdKey] = "offline-test-project",
    };

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "CreateResourceGroup", """{ "resourceGroupName": "rg", "location": "eastus", "expiresInMinutes": 0 }""" };
        yield return new object[] { "CreateVirtualNetwork", """{ "vnetName": "v", "resourceGroupName": "rg", "location": "eastus", "expiresInMinutes": 99999 }""" };
        yield return new object[] { "CreateVM", """{ "vmName": "vm", "resourceGroup": "rg", "adminPassword": "x", "expiresInMinutes": "soon" }""" };
        yield return new object[] { "CreateVpc", """{ "vpcName": "v", "expiresInMinutes": -1 }""" };
        yield return new object[] { "CreateEc2Instance", """{ "instanceName": "i", "vpcName": "v", "ami": "ami-1", "expiresInMinutes": 0 }""" };
        yield return new object[] { "CreateS3Bucket", """{ "bucketName": "b", "expiresInMinutes": 0 }""" };
        yield return new object[] { "CreateGceInstance", """{ "instanceName": "i", "networkName": "n", "zone": "us-central1-a", "expiresInMinutes": 0 }""" };
        yield return new object[] { "CreateGcsBucket", """{ "bucketName": "b", "expiresInMinutes": 0 }""" };
        yield return new object[] { "DeployCloudRunService", """{ "serviceName": "s", "region": "us-central1", "image": "img", "expiresInMinutes": 0 }""" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InvalidExpiresInMinutes_FailsBeforeAnySdkCall(string operation, string parametersJson)
    {
        var handler = CreateHandler(operation);
        var parameters = JsonDocument.Parse(parametersJson).RootElement;

        var result = await handler.ExecuteAsync("layer", parameters, new Dictionary<string, string>(RunEnv));

        Assert.False(result.Success);
        Assert.Contains("expiresInMinutes", result.Output);
    }

    private static IOperationHandler CreateHandler(string operation) => operation switch
    {
        "CreateResourceGroup" => new CreateResourceGroupHandler(Mock.Of<ILogger<CreateResourceGroupHandler>>()),
        "CreateVirtualNetwork" => new CreateVirtualNetworkHandler(Mock.Of<ILogger<CreateVirtualNetworkHandler>>()),
        "CreateVM" => new CreateVMHandler(Mock.Of<ILogger<CreateVMHandler>>()),
        "CreateVpc" => new CreateVpcHandler(Mock.Of<ILogger<CreateVpcHandler>>()),
        "CreateEc2Instance" => new CreateEc2InstanceHandler(Mock.Of<ILogger<CreateEc2InstanceHandler>>()),
        "CreateS3Bucket" => new CreateS3BucketHandler(Mock.Of<ILogger<CreateS3BucketHandler>>()),
        "CreateGceInstance" => new CreateGceInstanceHandler(Mock.Of<ILogger<CreateGceInstanceHandler>>()),
        "CreateGcsBucket" => new CreateGcsBucketHandler(Mock.Of<ILogger<CreateGcsBucketHandler>>()),
        "DeployCloudRunService" => new DeployCloudRunServiceHandler(Mock.Of<ILogger<DeployCloudRunServiceHandler>>()),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
