using Aura.Core.Interfaces;

namespace Aura.Worker.Services;

public class WorkerTenantContext : ITenantContext
{
    public Guid TenantId { get; set; } = Guid.Empty;
}
