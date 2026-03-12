using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Member,Operator")]
[Route("api/v1/essences")]
public class EssencesController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public EssencesController(AuraDbContext db, ITenantContext tenant, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        (offset, limit) = PaginationDefaults.Clamp(offset, limit);
        var query = _db.Essences.OrderBy(e => e.CreatedAt);
        var total = await query.CountAsync();
        var items = await query.Skip(offset).Take(limit)
            .Select(e => ToDto(e)).ToListAsync();

        return Ok(new PaginatedResponse<EssenceResponse>(items, total, offset, limit));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var essence = await _db.Essences.FindAsync(id);
        if (essence is null)
            return NotFound(new ErrorResponse("not_found", "Essence not found.", 404));

        return Ok(ToDto(essence));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Member")]
    public async Task<IActionResult> Create([FromBody] CreateEssenceRequest request)
    {
        var accountExists = await _db.CloudAccounts.AnyAsync(c => c.Id == request.CloudAccountId);
        if (!accountExists)
            return BadRequest(new ErrorResponse("bad_request", "Cloud account not found.", 400));

        var essence = new Essence
        {
            TenantId = _tenant.TenantId,
            Name = request.Name,
            CloudAccountId = request.CloudAccountId,
            EssenceJson = request.EssenceJson,
            CurrentVersion = 1
        };

        _db.Essences.Add(essence);

        // Save initial version
        _db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = essence.Id,
            VersionNumber = 1,
            EssenceJson = request.EssenceJson,
            ChangedByUserId = GetCurrentUserId()
        });

        await _db.SaveChangesAsync();

        await _audit.LogAsync(_tenant.TenantId, GetCurrentUserId(), "create", "Essence", essence.Id);

        return CreatedAtAction(nameof(Get), new { id = essence.Id }, ToDto(essence));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Member")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEssenceRequest request)
    {
        var essence = await _db.Essences.FindAsync(id);
        if (essence is null)
            return NotFound(new ErrorResponse("not_found", "Essence not found.", 404));

        if (request.Name is not null)
            essence.Name = request.Name;

        if (request.CloudAccountId.HasValue)
        {
            var accountExists = await _db.CloudAccounts.AnyAsync(c => c.Id == request.CloudAccountId.Value);
            if (!accountExists)
                return BadRequest(new ErrorResponse("bad_request", "Cloud account not found.", 400));
            essence.CloudAccountId = request.CloudAccountId.Value;
        }

        if (request.EssenceJson is not null)
        {
            essence.EssenceJson = request.EssenceJson;
            essence.CurrentVersion++;

            _db.EssenceVersions.Add(new EssenceVersion
            {
                EssenceId = essence.Id,
                VersionNumber = essence.CurrentVersion,
                EssenceJson = request.EssenceJson,
                ChangedByUserId = GetCurrentUserId()
            });
        }

        essence.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(_tenant.TenantId, GetCurrentUserId(), "update", "Essence", essence.Id,
            $"v{essence.CurrentVersion}");

        return Ok(ToDto(essence));
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<IActionResult> ListVersions(Guid id,
        [FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        (offset, limit) = PaginationDefaults.Clamp(offset, limit);

        var exists = await _db.Essences.AnyAsync(e => e.Id == id);
        if (!exists)
            return NotFound(new ErrorResponse("not_found", "Essence not found.", 404));

        var query = _db.EssenceVersions
            .Where(v => v.EssenceId == id)
            .OrderByDescending(v => v.VersionNumber);

        var total = await query.CountAsync();
        var items = await query.Skip(offset).Take(limit)
            .Select(v => new EssenceVersionResponse(
                v.Id, v.VersionNumber, v.EssenceJson, v.ChangedByUserId, v.CreatedAt))
            .ToListAsync();

        return Ok(new PaginatedResponse<EssenceVersionResponse>(items, total, offset, limit));
    }

    [HttpGet("{id:guid}/versions/{versionNumber:int}")]
    public async Task<IActionResult> GetVersion(Guid id, int versionNumber)
    {
        var version = await _db.EssenceVersions
            .FirstOrDefaultAsync(v => v.EssenceId == id && v.VersionNumber == versionNumber);

        if (version is null)
            return NotFound(new ErrorResponse("not_found", "Version not found.", 404));

        return Ok(new EssenceVersionResponse(
            version.Id, version.VersionNumber, version.EssenceJson,
            version.ChangedByUserId, version.CreatedAt));
    }

    [HttpPost("{id:guid}/clone")]
    [Authorize(Roles = "Admin,Member")]
    public async Task<IActionResult> Clone(Guid id, [FromBody] CloneEssenceRequest request)
    {
        var source = await _db.Essences.FindAsync(id);
        if (source is null)
            return NotFound(new ErrorResponse("not_found", "Essence not found.", 404));

        var clone = new Essence
        {
            TenantId = _tenant.TenantId,
            Name = request.Name,
            CloudAccountId = source.CloudAccountId,
            EssenceJson = source.EssenceJson,
            CurrentVersion = 1
        };

        _db.Essences.Add(clone);

        _db.EssenceVersions.Add(new EssenceVersion
        {
            EssenceId = clone.Id,
            VersionNumber = 1,
            EssenceJson = source.EssenceJson,
            ChangedByUserId = GetCurrentUserId()
        });

        await _db.SaveChangesAsync();

        await _audit.LogAsync(_tenant.TenantId, GetCurrentUserId(), "clone", "Essence", clone.Id,
            $"from={id}");

        return CreatedAtAction(nameof(Get), new { id = clone.Id }, ToDto(clone));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,Member")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var essence = await _db.Essences.FindAsync(id);
        if (essence is null)
            return NotFound(new ErrorResponse("not_found", "Essence not found.", 404));

        _db.Essences.Remove(essence);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(_tenant.TenantId, GetCurrentUserId(), "delete", "Essence", id);

        return NoContent();
    }

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)
            ?? User.FindFirst("sub");
        return sub is not null ? Guid.Parse(sub.Value) : Guid.Empty;
    }

    private static EssenceResponse ToDto(Essence e) =>
        new(e.Id, e.Name, e.CloudAccountId, e.EssenceJson, e.CurrentVersion, e.CreatedAt, e.UpdatedAt);
}
