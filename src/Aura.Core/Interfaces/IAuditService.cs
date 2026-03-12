namespace Aura.Core.Interfaces;

public interface IAuditService
{
    Task LogAsync(Guid tenantId, Guid userId, string action, string entityType, Guid? entityId = null, string? detail = null, CancellationToken ct = default);
}
