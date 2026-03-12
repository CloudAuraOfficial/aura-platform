using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/v1/audit-log")]
public class AuditLogController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;

    public AuditLogController(AuraDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 25,
        [FromQuery] string? entityType = null,
        [FromQuery] Guid? entityId = null)
    {
        (offset, limit) = PaginationDefaults.Clamp(offset, limit);

        var query = _db.AuditLog
            .Where(a => a.TenantId == _tenant.TenantId);

        if (entityType is not null)
            query = query.Where(a => a.EntityType == entityType);

        if (entityId.HasValue)
            query = query.Where(a => a.EntityId == entityId.Value);

        var ordered = query.OrderByDescending(a => a.CreatedAt);
        var total = await ordered.CountAsync();
        var items = await ordered.Skip(offset).Take(limit)
            .Select(a => new AuditLogResponse(
                a.Id, a.UserId, a.Action, a.EntityType, a.EntityId, a.Detail, a.CreatedAt))
            .ToListAsync();

        return Ok(new PaginatedResponse<AuditLogResponse>(items, total, offset, limit));
    }
}
