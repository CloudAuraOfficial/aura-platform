using Aura.Core.Enums;
using Aura.Worker.Operations.Common;
using Aura.Worker.Operations.Gcp;
using Google.Api.Gax.Grpc;
using Google.Cloud.Compute.V1;

namespace Aura.Worker.Reaper.Providers;

/// <summary>
/// GCP: Compute Engine instances labelled aura-expires-at, across all zones of the project.
/// Buckets, Cloud Run services, networks and firewall rules are not reaped (known gap; networks and
/// firewall rules do not support labels).
/// </summary>
public sealed class GcpResourceReaper : IProviderResourceReaper
{
    private const int InstanceRank = 10;
    private const string ZonePrefix = "zones/";

    public CloudProvider Provider => CloudProvider.Gcp;

    public async Task<IReadOnlyList<ReapableResource>> ListTaggedAsync(
        Dictionary<string, string> envVars, CancellationToken ct)
    {
        var project = GcpClientFactory.ResolveProjectId(envVars);
        var client = GcpClientFactory.CreateInstances(envVars);

        // The aggregated list is filtered client-side on the label: the server-side label filter syntax
        // for hyphenated keys is not something this code can verify offline.
        var results = new List<ReapableResource>();
        var pages = client.AggregatedListAsync(
            new AggregatedListInstancesRequest { Project = project },
            CallSettings.FromCancellationToken(ct));

        await foreach (var zone in pages)
        {
            var zoneName = zone.Key.StartsWith(ZonePrefix, StringComparison.Ordinal)
                ? zone.Key[ZonePrefix.Length..]
                : zone.Key;

            foreach (var instance in zone.Value.Instances)
            {
                if (!instance.Labels.TryGetValue(ResourceTags.ExpiresAtKey, out var expiresAt))
                    continue;

                results.Add(new ReapableResource(
                    CloudProvider.Gcp, "compute:instance", instance.Name, zoneName, InstanceRank, expiresAt));
            }
        }
        return results;
    }

    public async Task DeleteAsync(ReapableResource resource, Dictionary<string, string> envVars, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(resource.Location))
            throw new InvalidOperationException($"Instance {resource.Id} has no zone; refusing to delete.");

        var project = GcpClientFactory.ResolveProjectId(envVars);
        var client = GcpClientFactory.CreateInstances(envVars);
        var op = await client.DeleteAsync(project, resource.Location, resource.Id, ct);
        await op.PollUntilCompletedAsync();
    }
}
