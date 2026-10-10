using Aura.Worker.Operations.Aws;
using Aura.Worker.Operations.Azure;
using Aura.Worker.Operations.Common;
using Aura.Worker.Operations.Gcp;

namespace Aura.Worker.Operations;

public class OperationRegistry
{
    private readonly Dictionary<string, Type> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void Register<THandler>(string operationType) where THandler : IOperationHandler
    {
        _handlers[operationType] = typeof(THandler);
    }

    public bool HasHandler(string operationType) =>
        _handlers.ContainsKey(operationType);

    /// <summary>Operation types with a registered handler. Compared against Aura.Core's OperationTypes.All in tests.</summary>
    public IReadOnlyCollection<string> RegisteredOperationTypes => _handlers.Keys;

    /// <summary>The production registry: a handler for every operation type the Worker executes.</summary>
    public static OperationRegistry CreateDefault()
    {
        var registry = new OperationRegistry();
        registry.Register<CreateResourceGroupHandler>("CreateResourceGroup");
        registry.Register<CreateContainerRegistryHandler>("CreateContainerRegistry");
        registry.Register<BuildContainerImageHandler>("BuildContainerImage");
        registry.Register<PushContainerImageHandler>("PushContainerImage");
        registry.Register<ImportContainerImageHandler>("ImportContainerImage");
        registry.Register<CreateContainerGroupHandler>("CreateContainerGroup");
        registry.Register<StopContainerGroupHandler>("StopContainerGroup");
        registry.Register<DeleteContainerGroupHandler>("DeleteContainerGroup");
        registry.Register<HttpHealthCheckHandler>("HttpHealthCheck");
        registry.Register<CreateVMHandler>("CreateVM");
        registry.Register<StartVMHandler>("StartVM");
        registry.Register<StopVMHandler>("StopVM");
        registry.Register<DeleteVMHandler>("DeleteVM");
        registry.Register<CreateVirtualNetworkHandler>("CreateVirtualNetwork");
        registry.Register<DeleteVirtualNetworkHandler>("DeleteVirtualNetwork");
        registry.Register<DeployArmTemplateHandler>("DeployArmTemplate");
        registry.Register<DeleteResourceGroupHandler>("DeleteResourceGroup");
        registry.Register<CreateVpcHandler>("CreateVpc");
        registry.Register<DeleteVpcHandler>("DeleteVpc");
        registry.Register<CreateEc2InstanceHandler>("CreateEc2Instance");
        registry.Register<StartEc2InstanceHandler>("StartEc2Instance");
        registry.Register<StopEc2InstanceHandler>("StopEc2Instance");
        registry.Register<TerminateEc2InstanceHandler>("TerminateEc2Instance");
        registry.Register<CreateS3BucketHandler>("CreateS3Bucket");
        registry.Register<DeleteS3BucketHandler>("DeleteS3Bucket");
        registry.Register<RunEcsTaskHandler>("RunEcsTask");
        registry.Register<DeployCloudFormationHandler>("DeployCloudFormation");
        registry.Register<CreateIamRoleHandler>("CreateIamRole");
        registry.Register<CreateNetworkHandler>("CreateNetwork");
        registry.Register<DeleteNetworkHandler>("DeleteNetwork");
        registry.Register<CreateGceInstanceHandler>("CreateGceInstance");
        registry.Register<StartGceInstanceHandler>("StartGceInstance");
        registry.Register<StopGceInstanceHandler>("StopGceInstance");
        registry.Register<DeleteGceInstanceHandler>("DeleteGceInstance");
        registry.Register<CreateGcsBucketHandler>("CreateGcsBucket");
        registry.Register<DeleteGcsBucketHandler>("DeleteGcsBucket");
        registry.Register<CreateFirewallRuleHandler>("CreateFirewallRule");
        registry.Register<DeployCloudRunServiceHandler>("DeployCloudRunService");
        registry.Register<CreateServiceAccountHandler>("CreateServiceAccount");
        return registry;
    }

    public IOperationHandler Resolve(IServiceProvider sp, string operationType)
    {
        if (!_handlers.TryGetValue(operationType, out var handlerType))
            throw new InvalidOperationException(
                $"No handler registered for operation type '{operationType}'.");

        return (IOperationHandler)sp.GetRequiredService(handlerType);
    }
}
