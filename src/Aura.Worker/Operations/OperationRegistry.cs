using Aura.Worker.Operations.Aws;
using Aura.Worker.Operations.Azure;
using Aura.Worker.Operations.Common;
using Aura.Worker.Operations.Gcp;
using Microsoft.Extensions.DependencyInjection;

namespace Aura.Worker.Operations;

public class OperationRegistry
{
    private readonly Dictionary<string, Type> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public void Register<THandler>(string operationType) where THandler : IOperationHandler
    {
        _handlers[operationType] = typeof(THandler);
    }

    private void Add<THandler>(string operationType, IServiceCollection services) where THandler : class, IOperationHandler
    {
        Register<THandler>(operationType);
        services.AddTransient<THandler>();
    }

    public bool HasHandler(string operationType) =>
        _handlers.ContainsKey(operationType);

    /// <summary>Operation types with a registered handler. Compared against Aura.Core's OperationTypes.All in tests.</summary>
    public IReadOnlyCollection<string> RegisteredOperationTypes => _handlers.Keys;

    /// <summary>
    /// The production registry: a handler for every operation type the Worker executes. Each handler is
    /// registered in DI here too, so the registry and the container cannot drift apart.
    /// </summary>
    public static OperationRegistry CreateDefault(IServiceCollection services)
    {
        var registry = new OperationRegistry();
        registry.Add<CreateResourceGroupHandler>("CreateResourceGroup", services);
        registry.Add<CreateContainerRegistryHandler>("CreateContainerRegistry", services);
        registry.Add<BuildContainerImageHandler>("BuildContainerImage", services);
        registry.Add<PushContainerImageHandler>("PushContainerImage", services);
        registry.Add<ImportContainerImageHandler>("ImportContainerImage", services);
        registry.Add<CreateContainerGroupHandler>("CreateContainerGroup", services);
        registry.Add<StopContainerGroupHandler>("StopContainerGroup", services);
        registry.Add<DeleteContainerGroupHandler>("DeleteContainerGroup", services);
        registry.Add<HttpHealthCheckHandler>("HttpHealthCheck", services);
        registry.Add<CreateVMHandler>("CreateVM", services);
        registry.Add<StartVMHandler>("StartVM", services);
        registry.Add<StopVMHandler>("StopVM", services);
        registry.Add<DeleteVMHandler>("DeleteVM", services);
        registry.Add<CreateVirtualNetworkHandler>("CreateVirtualNetwork", services);
        registry.Add<DeleteVirtualNetworkHandler>("DeleteVirtualNetwork", services);
        registry.Add<DeployArmTemplateHandler>("DeployArmTemplate", services);
        registry.Add<DeleteResourceGroupHandler>("DeleteResourceGroup", services);
        registry.Add<CreateVpcHandler>("CreateVpc", services);
        registry.Add<DeleteVpcHandler>("DeleteVpc", services);
        registry.Add<CreateEc2InstanceHandler>("CreateEc2Instance", services);
        registry.Add<StartEc2InstanceHandler>("StartEc2Instance", services);
        registry.Add<StopEc2InstanceHandler>("StopEc2Instance", services);
        registry.Add<TerminateEc2InstanceHandler>("TerminateEc2Instance", services);
        registry.Add<CreateS3BucketHandler>("CreateS3Bucket", services);
        registry.Add<DeleteS3BucketHandler>("DeleteS3Bucket", services);
        registry.Add<RunEcsTaskHandler>("RunEcsTask", services);
        registry.Add<DeployCloudFormationHandler>("DeployCloudFormation", services);
        registry.Add<CreateIamRoleHandler>("CreateIamRole", services);
        registry.Add<CreateNetworkHandler>("CreateNetwork", services);
        registry.Add<DeleteNetworkHandler>("DeleteNetwork", services);
        registry.Add<CreateGceInstanceHandler>("CreateGceInstance", services);
        registry.Add<StartGceInstanceHandler>("StartGceInstance", services);
        registry.Add<StopGceInstanceHandler>("StopGceInstance", services);
        registry.Add<DeleteGceInstanceHandler>("DeleteGceInstance", services);
        registry.Add<CreateGcsBucketHandler>("CreateGcsBucket", services);
        registry.Add<DeleteGcsBucketHandler>("DeleteGcsBucket", services);
        registry.Add<CreateFirewallRuleHandler>("CreateFirewallRule", services);
        registry.Add<DeployCloudRunServiceHandler>("DeployCloudRunService", services);
        registry.Add<CreateServiceAccountHandler>("CreateServiceAccount", services);
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
