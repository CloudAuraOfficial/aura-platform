using Aura.Core.Enums;
using Aura.Core.Models;

namespace Aura.Core.Interfaces;

public interface IEmissionLoadResolver
{
    Task<EmissionLoadConfig> ResolveAsync(
        Guid tenantId,
        string tenantSlug,
        CloudProvider provider,
        string baseLoadName,
        CancellationToken ct = default);
}
