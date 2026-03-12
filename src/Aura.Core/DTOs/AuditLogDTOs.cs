namespace Aura.Core.DTOs;

public sealed record AuditLogResponse(
    Guid Id,
    Guid UserId,
    string Action,
    string EntityType,
    Guid? EntityId,
    string? Detail,
    DateTime CreatedAt
);
