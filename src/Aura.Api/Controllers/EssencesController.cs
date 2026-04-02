using System.Text.Json;
using Aura.Api.Middleware;
using Aura.Api.Services;
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
    private readonly AiEssenceBuilderService _aiBuilder;

    public EssencesController(AuraDbContext db, ITenantContext tenant, IAuditService audit,
        AiEssenceBuilderService aiBuilder)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
        _aiBuilder = aiBuilder;
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

    [HttpGet("{id:guid}/versions/{v1:int}/diff/{v2:int}")]
    public async Task<IActionResult> DiffVersions(Guid id, int v1, int v2)
    {
        var version1 = await _db.EssenceVersions
            .FirstOrDefaultAsync(v => v.EssenceId == id && v.VersionNumber == v1);
        var version2 = await _db.EssenceVersions
            .FirstOrDefaultAsync(v => v.EssenceId == id && v.VersionNumber == v2);

        if (version1 is null || version2 is null)
            return NotFound(new ErrorResponse("not_found", "One or both versions not found.", 404));

        var changes = ComputeJsonDiff(version1.EssenceJson, version2.EssenceJson, "");
        return Ok(new EssenceDiffResponse(v1, v2, changes));
    }

    internal static List<EssenceDiffEntry> ComputeJsonDiff(string fromJson, string toJson, string prefix)
    {
        var changes = new List<EssenceDiffEntry>();
        try
        {
            using var fromDoc = JsonDocument.Parse(fromJson);
            using var toDoc = JsonDocument.Parse(toJson);
            CompareElements(fromDoc.RootElement, toDoc.RootElement, prefix, changes);
        }
        catch (JsonException)
        {
            if (fromJson != toJson)
                changes.Add(new EssenceDiffEntry(prefix.Length == 0 ? "$" : prefix, "modified", fromJson, toJson));
        }
        return changes;
    }

    private static void CompareElements(JsonElement from, JsonElement to, string path, List<EssenceDiffEntry> changes)
    {
        if (from.ValueKind != to.ValueKind)
        {
            changes.Add(new EssenceDiffEntry(path.Length == 0 ? "$" : path, "modified",
                from.GetRawText(), to.GetRawText()));
            return;
        }

        switch (from.ValueKind)
        {
            case JsonValueKind.Object:
                var fromProps = new HashSet<string>();
                foreach (var prop in from.EnumerateObject())
                {
                    fromProps.Add(prop.Name);
                    var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                    if (to.TryGetProperty(prop.Name, out var toProp))
                        CompareElements(prop.Value, toProp, childPath, changes);
                    else
                        changes.Add(new EssenceDiffEntry(childPath, "removed", prop.Value.GetRawText(), null));
                }
                foreach (var prop in to.EnumerateObject())
                {
                    if (!fromProps.Contains(prop.Name))
                    {
                        var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                        changes.Add(new EssenceDiffEntry(childPath, "added", null, prop.Value.GetRawText()));
                    }
                }
                break;

            case JsonValueKind.Array:
                var fromArr = from.EnumerateArray().ToList();
                var toArr = to.EnumerateArray().ToList();
                var maxLen = Math.Max(fromArr.Count, toArr.Count);
                for (int i = 0; i < maxLen; i++)
                {
                    var itemPath = $"{path}[{i}]";
                    if (i >= fromArr.Count)
                        changes.Add(new EssenceDiffEntry(itemPath, "added", null, toArr[i].GetRawText()));
                    else if (i >= toArr.Count)
                        changes.Add(new EssenceDiffEntry(itemPath, "removed", fromArr[i].GetRawText(), null));
                    else
                        CompareElements(fromArr[i], toArr[i], itemPath, changes);
                }
                break;

            default:
                if (from.GetRawText() != to.GetRawText())
                    changes.Add(new EssenceDiffEntry(path.Length == 0 ? "$" : path, "modified",
                        from.GetRawText(), to.GetRawText()));
                break;
        }
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

    [HttpPost("generate")]
    [Authorize(Roles = "Admin,Member")]
    public async Task<IActionResult> Generate([FromBody] GenerateEssenceRequest request)
    {
        var accountExists = await _db.CloudAccounts.AnyAsync(c => c.Id == request.CloudAccountId);
        if (!accountExists)
            return BadRequest(new ErrorResponse("bad_request", "Cloud account not found.", 400));

        try
        {
            var result = await _aiBuilder.GenerateAsync(
                GetCurrentUserId(), request, _tenant.TenantId, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse("bad_request", ex.Message, 400));
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new ErrorResponse("generation_failed", ex.Message, 422));
        }
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
