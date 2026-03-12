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
[Authorize]
[Route("api/v1/essences")]
public class EssencesController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;

    public EssencesController(AuraDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
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
            EssenceJson = request.EssenceJson
        };

        _db.Essences.Add(essence);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = essence.Id }, ToDto(essence));
    }

    [HttpPut("{id:guid}")]
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
            essence.EssenceJson = request.EssenceJson;

        essence.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(ToDto(essence));
    }

    [HttpPost("{id:guid}/clone")]
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
            EssenceJson = source.EssenceJson
        };

        _db.Essences.Add(clone);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = clone.Id }, ToDto(clone));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var essence = await _db.Essences.FindAsync(id);
        if (essence is null)
            return NotFound(new ErrorResponse("not_found", "Essence not found.", 404));

        _db.Essences.Remove(essence);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static EssenceResponse ToDto(Essence e) =>
        new(e.Id, e.Name, e.CloudAccountId, e.EssenceJson, e.CreatedAt, e.UpdatedAt);
}
