using System.Text.Json;
using Amazon.EC2;
using Amazon.EC2.Model;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// Provisions a public VPC: VPC + public subnet + internet gateway + route table
/// with a default route to the IGW. Tags every resource with the layerName so
/// teardown (Epic 1 DeleteVpcHandler) can find them.
///
/// Parameters:
///   vpcName       (required)  — tag:Name applied to the VPC
///   cidrBlock     (optional, default "10.0.0.0/16")
///   subnetCidr    (optional, default "10.0.0.0/24")
///   availabilityZone (optional, default "<region>a")
/// </summary>
public class CreateVpcHandler : IOperationHandler
{
    private readonly ILogger<CreateVpcHandler> _logger;

    public CreateVpcHandler(ILogger<CreateVpcHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("vpcName", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: vpcName");

        var vpcName = nameProp.GetString()!;
        var cidr = parameters.TryGetProperty("cidrBlock", out var c) ? c.GetString()! : "10.0.0.0/16";
        var subnetCidr = parameters.TryGetProperty("subnetCidr", out var s) ? s.GetString()! : "10.0.0.0/24";

        using var ec2 = AwsClientFactory.CreateEc2(envVars);
        var az = parameters.TryGetProperty("availabilityZone", out var azProp)
            ? azProp.GetString()!
            : $"{ec2.Config.RegionEndpoint?.SystemName ?? "us-east-1"}a";

        try
        {
            _logger.LogInformation("Creating VPC {VpcName} ({Cidr}) in {Region}", vpcName, cidr, az);
            var vpc = (await ec2.CreateVpcAsync(new CreateVpcRequest
            {
                CidrBlock = cidr,
                TagSpecifications = TagSpecs(ResourceType.Vpc, vpcName, layerName),
            }, ct)).Vpc;

            _logger.LogInformation("Creating subnet {Cidr} for VPC {VpcId} in {Az}", subnetCidr, vpc.VpcId, az);
            var subnet = (await ec2.CreateSubnetAsync(new CreateSubnetRequest
            {
                VpcId = vpc.VpcId,
                CidrBlock = subnetCidr,
                AvailabilityZone = az,
                TagSpecifications = TagSpecs(ResourceType.Subnet, $"{vpcName}-subnet", layerName),
            }, ct)).Subnet;

            _logger.LogInformation("Creating internet gateway for VPC {VpcId}", vpc.VpcId);
            var igw = (await ec2.CreateInternetGatewayAsync(new CreateInternetGatewayRequest
            {
                TagSpecifications = TagSpecs(ResourceType.InternetGateway, $"{vpcName}-igw", layerName),
            }, ct)).InternetGateway;

            await ec2.AttachInternetGatewayAsync(new AttachInternetGatewayRequest
            {
                VpcId = vpc.VpcId,
                InternetGatewayId = igw.InternetGatewayId,
            }, ct);

            _logger.LogInformation("Creating route table for VPC {VpcId}", vpc.VpcId);
            var rt = (await ec2.CreateRouteTableAsync(new CreateRouteTableRequest
            {
                VpcId = vpc.VpcId,
                TagSpecifications = TagSpecs(ResourceType.RouteTable, $"{vpcName}-rt", layerName),
            }, ct)).RouteTable;

            await ec2.CreateRouteAsync(new CreateRouteRequest
            {
                RouteTableId = rt.RouteTableId,
                DestinationCidrBlock = "0.0.0.0/0",
                GatewayId = igw.InternetGatewayId,
            }, ct);

            await ec2.AssociateRouteTableAsync(new AssociateRouteTableRequest
            {
                RouteTableId = rt.RouteTableId,
                SubnetId = subnet.SubnetId,
            }, ct);

            return new LayerExecutionResult(true,
                $"VPC '{vpcName}' created: vpcId={vpc.VpcId}, subnetId={subnet.SubnetId}, igwId={igw.InternetGatewayId}, routeTableId={rt.RouteTableId}");
        }
        catch (AmazonEC2Exception ex)
        {
            _logger.LogError(ex, "Failed to create VPC {VpcName}", vpcName);
            return new LayerExecutionResult(false, $"Failed to create VPC: {ex.ErrorCode} — {ex.Message}");
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
