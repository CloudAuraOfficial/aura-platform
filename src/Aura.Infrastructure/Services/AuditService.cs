using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly AuraDbContext _db;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AuraDbContext db, ILogger<AuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(Guid tenantId, Guid userId, string action, string entityType, Guid? entityId = null, string? detail = null, CancellationToken ct = default)
    {
        var entry = new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Detail = detail
        };

        _db.AuditLog.Add(entry);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit: {Action} {EntityType} {EntityId} by user {UserId}",
            action, entityType, entityId, userId);
    }
}
