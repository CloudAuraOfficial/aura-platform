using Aura.Worker.Operations.Common;

namespace Aura.Worker.Reaper;

/// <summary>
/// Pure selection logic for the reaper: which tagged resources are past their expiry, and in what order to delete them.
/// Kept free of I/O so it can be unit-tested without a cloud.
/// </summary>
public static class ResourceReaperPlanner
{
    /// <summary>
    /// Returns the expired resources, dependency-safe ordered. A resource is expired when its
    /// aura-expires-at is valid UTC and at or before <paramref name="utcNow"/>. Missing or malformed
    /// values are never selected, so an unreadable tag cannot cause a delete.
    /// </summary>
    public static IReadOnlyList<ReapableResource> SelectExpired(
        IEnumerable<ReapableResource> candidates, DateTime utcNow)
    {
        return candidates
            .Where(r => ResourceTags.TryParseExpiry(r.ExpiresAtTag, out var expiresAt) && expiresAt <= utcNow)
            .DistinctBy(r => (r.Provider, r.Id))
            .OrderBy(r => r.DeletionRank)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }
}
