namespace Aura.Core.Operations;

/// <summary>
/// The operation types an essence layer may reference (<c>operationType</c>).
/// Shared by the API (generation and save validation) and the Worker, whose
/// <c>OperationRegistry</c> must register a handler for exactly this set; a test enforces that.
/// Matching is case-insensitive, the same as the Worker's registry lookup.
/// </summary>
public static class OperationTypes
{
    public static IReadOnlyList<string> All { get; } =
    [
        // Azure
        "CreateResourceGroup", "DeleteResourceGroup", "CreateVM", "StartVM", "StopVM", "DeleteVM",
        "CreateVirtualNetwork", "DeleteVirtualNetwork", "CreateContainerGroup", "StopContainerGroup",
        "DeleteContainerGroup", "CreateContainerRegistry", "BuildContainerImage", "PushContainerImage",
        "ImportContainerImage", "DeployArmTemplate", "HttpHealthCheck",

        // AWS
        "CreateVpc", "DeleteVpc", "CreateEc2Instance", "StartEc2Instance", "StopEc2Instance",
        "TerminateEc2Instance", "CreateS3Bucket", "DeleteS3Bucket", "RunEcsTask",
        "DeployCloudFormation", "CreateIamRole",

        // GCP
        "CreateNetwork", "DeleteNetwork", "CreateGceInstance", "StartGceInstance", "StopGceInstance",
        "DeleteGceInstance", "CreateGcsBucket", "DeleteGcsBucket", "CreateFirewallRule",
        "DeployCloudRunService", "CreateServiceAccount",
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="operationType"/> names a registered operation. Null, empty and untrimmed values are not valid.</summary>
    public static bool IsValid(string? operationType) =>
        operationType is not null && Known.Contains(operationType);
}
