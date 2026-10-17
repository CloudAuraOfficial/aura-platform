using Aura.Core.Enums;
using Aura.Worker.Operations.Azure;
using Aura.Worker.Operations.Common;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;

namespace Aura.Worker.Reaper.Providers;

/// <summary>
/// Azure: one tag-filtered generic-resource listing per subscription, covering every tagged resource type.
/// Resource groups are not generic resources, so they are not reaped (known gap).
/// </summary>
public sealed class AzureResourceReaper : IProviderResourceReaper
{
    public CloudProvider Provider => CloudProvider.Azure;

    public async Task<IReadOnlyList<ReapableResource>> ListTaggedAsync(
        Dictionary<string, string> envVars, CancellationToken ct)
    {
        var arm = AzureClientFactory.Create(envVars);
        var subscription = await arm.GetDefaultSubscriptionAsync(ct);

        var results = new List<ReapableResource>();
        await foreach (var resource in subscription.GetGenericResourcesAsync(
            filter: $"tagName eq '{ResourceTags.ExpiresAtKey}'", cancellationToken: ct))
        {
            if (!resource.Data.Tags.TryGetValue(ResourceTags.ExpiresAtKey, out var expiresAt))
                continue;

            var type = resource.Data.ResourceType.ToString();
            results.Add(new ReapableResource(
                CloudProvider.Azure, type, resource.Id.ToString(), resource.Data.Location.Name,
                DeletionRank(type), expiresAt));
        }
        return results;
    }

    public async Task DeleteAsync(ReapableResource resource, Dictionary<string, string> envVars, CancellationToken ct)
    {
        var arm = AzureClientFactory.Create(envVars);
        await arm.GetGenericResource(new ResourceIdentifier(resource.Id))
            .DeleteAsync(WaitUntil.Completed, ct);
    }

    /// <summary>Compute and container workloads first, then NICs, then the IPs and NSGs they reference, then VNets.</summary>
    internal static int DeletionRank(string resourceType) => resourceType.ToLowerInvariant() switch
    {
        "microsoft.compute/virtualmachines" => 10,
        "microsoft.containerinstance/containergroups" => 10,
        "microsoft.network/networkinterfaces" => 20,
        "microsoft.network/publicipaddresses" => 30,
        "microsoft.network/networksecuritygroups" => 30,
        "microsoft.network/virtualnetworks" => 40,
        _ => 50,
    };
}
