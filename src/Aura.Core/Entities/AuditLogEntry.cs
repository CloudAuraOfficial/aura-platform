namespace Aura.Core.Entities;

public class AuditLogEntry : BaseEntity
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? Detail { get; set; }
}
