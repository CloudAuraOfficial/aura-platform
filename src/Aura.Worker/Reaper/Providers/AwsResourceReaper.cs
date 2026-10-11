using Amazon.EC2;
using Amazon.EC2.Model;
using Aura.Core.Enums;
using Aura.Worker.Operations.Aws;
using Aura.Worker.Operations.Common;

namespace Aura.Worker.Reaper.Providers;

/// <summary>
/// AWS (region from the account's AWS_REGION / AURA_DEFAULT_REGION): EC2 instances tagged aura-expires-at.
/// VPCs, subnets, gateways, route tables, security groups and S3 buckets are not reaped (known gap).
/// </summary>
public sealed class AwsResourceReaper : IProviderResourceReaper
{
    private const int InstanceRank = 10;

    public CloudProvider Provider => CloudProvider.Aws;

    public async Task<IReadOnlyList<ReapableResource>> ListTaggedAsync(
        Dictionary<string, string> envVars, CancellationToken ct)
    {
        using var ec2 = AwsClientFactory.CreateEc2(envVars);
        var region = AwsClientFactory.RegionName(envVars);

        var results = new List<ReapableResource>();
        string? nextToken = null;
        do
        {
            var page = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest
            {
                Filters = new List<Filter>
                {
                    new() { Name = "tag-key", Values = new List<string> { ResourceTags.ExpiresAtKey } },
                },
                NextToken = nextToken,
            }, ct);

            foreach (var instance in page.Reservations.SelectMany(r => r.Instances))
            {
                if (string.Equals(instance.State?.Name?.Value, "terminated", StringComparison.Ordinal))
                    continue;

                var expiry = instance.Tags.FirstOrDefault(t => t.Key == ResourceTags.ExpiresAtKey);
                if (expiry is null)
                    continue;

                results.Add(new ReapableResource(
                    CloudProvider.Aws, "ec2:instance", instance.InstanceId, region, InstanceRank, expiry.Value));
            }

            nextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        return results;
    }

    public async Task DeleteAsync(ReapableResource resource, Dictionary<string, string> envVars, CancellationToken ct)
    {
        using var ec2 = AwsClientFactory.CreateEc2(envVars);
        await ec2.TerminateInstancesAsync(new TerminateInstancesRequest
        {
            InstanceIds = new List<string> { resource.Id },
        }, ct);
    }
}
