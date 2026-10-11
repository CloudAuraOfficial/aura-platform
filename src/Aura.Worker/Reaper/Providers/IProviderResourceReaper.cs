using Aura.Core.Enums;

namespace Aura.Worker.Reaper.Providers;

/// <summary>
/// Lists and deletes tagged resources for one cloud provider. Implementations list only resources that
/// carry aura-expires-at and only delete what the planner selected. They never touch untagged resources.
/// </summary>
public interface IProviderResourceReaper
{
    CloudProvider Provider { get; }

    /// <summary>Resources in the account's default scope (subscription, project, region) that carry aura-expires-at.</summary>
    Task<IReadOnlyList<ReapableResource>> ListTaggedAsync(
        Dictionary<string, string> envVars, CancellationToken ct);

    Task DeleteAsync(ReapableResource resource, Dictionary<string, string> envVars, CancellationToken ct);
}
