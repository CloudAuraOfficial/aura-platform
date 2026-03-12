namespace Aura.Core.Entities;

public abstract class TenantScopedEntity : BaseEntity
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
}
