using System.Text.Json;
using Amazon.EC2;
using Amazon.EC2.Model;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// Tears down a VPC previously created by CreateVpcHandler. Finds the VPC
/// by its Name tag and removes child resources in the order AWS requires:
/// subnets → non-main route tables → IGW (detach then delete) → VPC.
///
/// Parameters:
///   vpcName  (required)  — matched against tag:Name on the VPC
/// </summary>
public class DeleteVpcHandler : IOperationHandler
{
    private readonly ILogger<DeleteVpcHandler> _logger;

    public DeleteVpcHandler(ILogger<DeleteVpcHandler> logger)
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

        using var ec2 = AwsClientFactory.CreateEc2(envVars);

        try
        {
            var vpcs = (await ec2.DescribeVpcsAsync(new DescribeVpcsRequest
            {
                Filters = new List<Filter> { new() { Name = "tag:Name", Values = new List<string> { vpcName } } },
            }, ct)).Vpcs ?? new List<Vpc>();

            if (vpcs.Count == 0)
                return new LayerExecutionResult(true, $"VPC '{vpcName}' not found — nothing to delete.");

            var vpc = vpcs[0];
            var vpcId = vpc.VpcId;
            _logger.LogInformation("Deleting VPC {VpcName} ({VpcId})", vpcName, vpcId);

            // Subnets
            var subnets = (await ec2.DescribeSubnetsAsync(new DescribeSubnetsRequest
            {
                Filters = new List<Filter> { new() { Name = "vpc-id", Values = new List<string> { vpcId } } },
            }, ct)).Subnets ?? new List<Subnet>();
            foreach (var subnet in subnets)
            {
                _logger.LogInformation("  Deleting subnet {SubnetId}", subnet.SubnetId);
                await ec2.DeleteSubnetAsync(new DeleteSubnetRequest { SubnetId = subnet.SubnetId }, ct);
            }

            // Route tables (skip the main one — it goes with the VPC)
            var routeTables = (await ec2.DescribeRouteTablesAsync(new DescribeRouteTablesRequest
            {
                Filters = new List<Filter> { new() { Name = "vpc-id", Values = new List<string> { vpcId } } },
            }, ct)).RouteTables ?? new List<RouteTable>();
            foreach (var rt in routeTables)
            {
                // SDK returns null (not empty) for route tables with no associations
                var associations = rt.Associations ?? new List<RouteTableAssociation>();
                var isMain = associations.Any(a => a.Main == true);
                if (isMain) continue;

                // Drop any subnet associations first
                foreach (var assoc in associations.Where(a => !string.IsNullOrEmpty(a.RouteTableAssociationId)))
                {
                    await ec2.DisassociateRouteTableAsync(new DisassociateRouteTableRequest
                    {
                        AssociationId = assoc.RouteTableAssociationId,
                    }, ct);
                }
                _logger.LogInformation("  Deleting route table {RouteTableId}", rt.RouteTableId);
                await ec2.DeleteRouteTableAsync(new DeleteRouteTableRequest { RouteTableId = rt.RouteTableId }, ct);
            }

            // Internet gateways (detach then delete)
            var igws = (await ec2.DescribeInternetGatewaysAsync(new DescribeInternetGatewaysRequest
            {
                Filters = new List<Filter> { new() { Name = "attachment.vpc-id", Values = new List<string> { vpcId } } },
            }, ct)).InternetGateways ?? new List<InternetGateway>();
            foreach (var igw in igws)
            {
                _logger.LogInformation("  Detaching + deleting IGW {IgwId}", igw.InternetGatewayId);
                await ec2.DetachInternetGatewayAsync(new DetachInternetGatewayRequest
                {
                    InternetGatewayId = igw.InternetGatewayId,
                    VpcId = vpcId,
                }, ct);
                await ec2.DeleteInternetGatewayAsync(new DeleteInternetGatewayRequest
                {
                    InternetGatewayId = igw.InternetGatewayId,
                }, ct);
            }

            // Non-default security groups inside the VPC (default SG goes with the VPC)
            var sgs = (await ec2.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest
            {
                Filters = new List<Filter> { new() { Name = "vpc-id", Values = new List<string> { vpcId } } },
            }, ct)).SecurityGroups ?? new List<SecurityGroup>();
            foreach (var sg in sgs.Where(s => s.GroupName != "default"))
            {
                _logger.LogInformation("  Deleting security group {SgId}", sg.GroupId);
                await ec2.DeleteSecurityGroupAsync(new DeleteSecurityGroupRequest { GroupId = sg.GroupId }, ct);
            }

            // VPC itself
            await ec2.DeleteVpcAsync(new DeleteVpcRequest { VpcId = vpcId }, ct);

            return new LayerExecutionResult(true, $"VPC '{vpcName}' ({vpcId}) deleted along with {subnets.Count} subnet(s), {routeTables.Count} route table(s), {igws.Count} IGW(s).");
        }
        catch (AmazonEC2Exception ex)
        {
            _logger.LogError(ex, "Failed to delete VPC {VpcName}", vpcName);
            return new LayerExecutionResult(false, $"Failed to delete VPC: {ex.ErrorCode} — {ex.Message}");
        }
    }
}
