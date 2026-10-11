using Aura.Core.Enums;

namespace Aura.Core.Operations;

/// <summary>
/// The operation types an essence layer may reference (<c>operationType</c>).
/// Shared by the API (generation and save validation) and the Worker, whose
/// <c>OperationRegistry</c> registers a handler for exactly this set; a test enforces that.
/// Matching is case-insensitive. Stored names use the canonical casing below, because the
/// EmissionLoad entrypoints compare names case-sensitively.
/// </summary>
public static class OperationTypes
{
    private static readonly string[] CommonTypes = ["HttpHealthCheck"];

    private static readonly string[] AzureTypes =
    [
        "CreateResourceGroup", "DeleteResourceGroup", "CreateVM", "StartVM", "StopVM", "DeleteVM",
        "CreateVirtualNetwork", "DeleteVirtualNetwork", "CreateContainerGroup", "StopContainerGroup",
        "DeleteContainerGroup", "CreateContainerRegistry", "BuildContainerImage", "PushContainerImage",
        "ImportContainerImage", "DeployArmTemplate",
    ];

    private static readonly string[] AwsTypes =
    [
        "CreateVpc", "DeleteVpc", "CreateEc2Instance", "StartEc2Instance", "StopEc2Instance",
        "TerminateEc2Instance", "CreateS3Bucket", "DeleteS3Bucket", "RunEcsTask",
        "DeployCloudFormation", "CreateIamRole",
    ];

    private static readonly string[] GcpTypes =
    [
        "CreateNetwork", "DeleteNetwork", "CreateGceInstance", "StartGceInstance", "StopGceInstance",
        "DeleteGceInstance", "CreateGcsBucket", "DeleteGcsBucket", "CreateFirewallRule",
        "DeployCloudRunService", "CreateServiceAccount",
    ];

    // What the Azure EmissionLoad entrypoint dispatches on AURA_OPERATION_TYPE (see EmissionLoad/Aura/azure/entrypoint.sh).
    private static readonly string[] AzureEntrypointTypes =
    [
        "CreateResourceGroup", "DeleteResourceGroup", "CreateVM", "StartVM", "StopVM", "DeleteVM",
        "CreateContainerRegistry", "BuildContainerImage", "PushContainerImage", "ImportContainerImage",
        "CreateContainerGroup", "StopContainerGroup", "DeleteContainerGroup", "HttpHealthCheck",
    ];

    /// <summary>Every operation type the Worker can run, on any cloud.</summary>
    public static IReadOnlyList<string> All { get; } = [.. CommonTypes, .. AzureTypes, .. AwsTypes, .. GcpTypes];

    /// <summary>Operation types valid for an essence generated for <paramref name="cloud"/>.</summary>
    public static IReadOnlyList<string> ForCloud(CloudProvider cloud) => cloud switch
    {
        CloudProvider.Aws => [.. CommonTypes, .. AwsTypes],
        CloudProvider.Gcp => [.. CommonTypes, .. GcpTypes],
        _ => [.. CommonTypes, .. AzureTypes],
    };

    /// <summary>
    /// The operation types the EmissionLoad container for <paramref name="cloud"/> runs. The AWS and GCP
    /// entrypoints dispatch no operation types: they run <c>AURA_LAYER_COMMAND</c>, which the Worker never sets.
    /// </summary>
    public static IReadOnlyList<string> EmissionLoadEntrypoint(CloudProvider cloud) =>
        cloud == CloudProvider.Azure ? AzureEntrypointTypes : [];

    private static readonly Dictionary<string, string> Canonical =
        All.ToDictionary(t => t, t => t, StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical casing of <paramref name="operationType"/>, or null when it names no operation.</summary>
    public static string? Canonicalize(string? operationType) =>
        operationType is not null && Canonical.TryGetValue(operationType, out var canonical) ? canonical : null;

    /// <summary>True when <paramref name="operationType"/> names a registered operation. Null, empty and untrimmed values are not valid.</summary>
    public static bool IsValid(string? operationType) => Canonicalize(operationType) is not null;
}
