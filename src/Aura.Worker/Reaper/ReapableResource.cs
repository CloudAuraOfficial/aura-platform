using Aura.Core.Enums;

namespace Aura.Worker.Reaper;

/// <summary>
/// A cloud resource that carries an aura-expires-at tag, as seen by a provider lister.
/// <see cref="DeletionRank"/> orders deletes so dependents go before what they depend on
/// (lower deletes first): instances before NICs, NICs before IPs/NSGs, IPs/NSGs before VNets.
/// </summary>
public sealed record ReapableResource(
    CloudProvider Provider,
    string Kind,
    string Id,
    string? Location,
    int DeletionRank,
    string ExpiresAtTag);
