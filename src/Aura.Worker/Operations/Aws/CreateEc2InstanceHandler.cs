using System.Text.Json;
using Amazon.EC2;
using Amazon.EC2.Model;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// Launches an EC2 instance into a VPC previously created by CreateVpcHandler.
/// Discovers VPC + subnet by their Name tag (vpcName param) — same pattern
/// the Azure side uses with resource-group-as-scope.
///
/// Parameters:
///   instanceName   (required)  — tag:Name on the instance
///   vpcName        (required)  — used to look up VPC + first tagged subnet
///   ami            (required)  — image id, e.g. ami-0c7217cdde317cfec (Ubuntu 22.04 us-east-1)
///   instanceType   (optional, default "t3.micro")
///   keyName        (optional)  — EC2 key pair; SSH disabled if omitted
///   openPorts      (optional, default [22])
/// </summary>
public class CreateEc2InstanceHandler : IOperationHandler
{
    private readonly ILogger<CreateEc2InstanceHandler> _logger;

    public CreateEc2InstanceHandler(ILogger<CreateEc2InstanceHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("instanceName", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: instanceName");
        if (!parameters.TryGetProperty("vpcName", out var vpcProp) || vpcProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: vpcName");
        if (!parameters.TryGetProperty("ami", out var amiProp) || amiProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: ami");

        var instanceName = nameProp.GetString()!;
        var vpcName = vpcProp.GetString()!;
        var ami = amiProp.GetString()!;
        var instanceType = parameters.TryGetProperty("instanceType", out var itProp)
            ? itProp.GetString()! : "t3.micro";
        var keyName = parameters.TryGetProperty("keyName", out var kProp) ? kProp.GetString() : null;

        var openPorts = new List<int> { 22 };
        if (parameters.TryGetProperty("openPorts", out var ports) && ports.ValueKind == JsonValueKind.Array)
        {
            openPorts = ports.EnumerateArray().Select(p => p.GetInt32()).ToList();
        }

        using var ec2 = AwsClientFactory.CreateEc2(envVars);

        try
        {
            var vpcs = (await ec2.DescribeVpcsAsync(new DescribeVpcsRequest
            {
                Filters = new List<Filter> { new() { Name = "tag:Name", Values = new List<string> { vpcName } } },
            }, ct)).Vpcs;
            if (vpcs.Count == 0)
                return new LayerExecutionResult(false, $"VPC '{vpcName}' not found — run CreateVpc first.");
            var vpcId = vpcs[0].VpcId;

            var subnets = (await ec2.DescribeSubnetsAsync(new DescribeSubnetsRequest
            {
                Filters = new List<Filter> { new() { Name = "vpc-id", Values = new List<string> { vpcId } } },
            }, ct)).Subnets;
            if (subnets.Count == 0)
                return new LayerExecutionResult(false, $"VPC '{vpcName}' has no subnets.");
            var subnetId = subnets[0].SubnetId;

            _logger.LogInformation("Creating security group {Sg} in VPC {VpcId}", $"{instanceName}-sg", vpcId);
            var sg = await ec2.CreateSecurityGroupAsync(new CreateSecurityGroupRequest
            {
                GroupName = $"{instanceName}-sg",
                Description = $"SG for {instanceName}",
                VpcId = vpcId,
                TagSpecifications = TagSpecs(ResourceType.SecurityGroup, $"{instanceName}-sg", layerName),
            }, ct);

            await ec2.AuthorizeSecurityGroupIngressAsync(new AuthorizeSecurityGroupIngressRequest
            {
                GroupId = sg.GroupId,
                IpPermissions = openPorts.Select(port => new IpPermission
                {
                    IpProtocol = "tcp",
                    FromPort = port,
                    ToPort = port,
                    Ipv4Ranges = new List<IpRange> { new() { CidrIp = "0.0.0.0/0", Description = $"port {port}" } },
                }).ToList(),
            }, ct);

            _logger.LogInformation("Launching EC2 {InstanceName} ({InstanceType}, {Ami})", instanceName, instanceType, ami);
            var runReq = new RunInstancesRequest
            {
                ImageId = ami,
                InstanceType = InstanceType.FindValue(instanceType),
                MinCount = 1,
                MaxCount = 1,
                SubnetId = subnetId,
                SecurityGroupIds = new List<string> { sg.GroupId },
                TagSpecifications = TagSpecs(ResourceType.Instance, instanceName, layerName),
            };
            if (!string.IsNullOrEmpty(keyName)) runReq.KeyName = keyName;

            var instance = (await ec2.RunInstancesAsync(runReq, ct)).Reservation.Instances[0];

            return new LayerExecutionResult(true,
                $"EC2 instance '{instanceName}' launched: instanceId={instance.InstanceId}, subnet={subnetId}, sg={sg.GroupId}.");
        }
        catch (AmazonEC2Exception ex)
        {
            _logger.LogError(ex, "Failed to create EC2 instance {InstanceName}", instanceName);
            return new LayerExecutionResult(false, $"Failed to launch EC2: {ex.ErrorCode} — {ex.Message}");
        }
    }

    private static List<TagSpecification> TagSpecs(ResourceType resourceType, string nameTag, string layerName) =>
        new()
        {
            new TagSpecification
            {
                ResourceType = resourceType,
                Tags = new List<Tag>
                {
                    new() { Key = "Name", Value = nameTag },
                    new() { Key = "aura:layer", Value = layerName },
                    new() { Key = "aura:managed", Value = "true" },
                },
            },
        };
}
