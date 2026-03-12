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
[Route("api/v1/cloud-accounts")]
public class CloudAccountsController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICryptoService _crypto;

    public CloudAccountsController(AuraDbContext db, ITenantContext tenant, ICryptoService crypto)
    {
        _db = db;
        _tenant = tenant;
        _crypto = crypto;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        var query = _db.CloudAccounts.OrderBy(c => c.CreatedAt);
        var total = await query.CountAsync();
        var items = await query.Skip(offset).Take(limit)
            .Select(c => ToDto(c)).ToListAsync();

        return Ok(new PaginatedResponse<CloudAccountResponse>(items, total, offset, limit));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var account = await _db.CloudAccounts.FindAsync(id);
        if (account is null)
            return NotFound(new ErrorResponse("not_found", "Cloud account not found.", 404));

        return Ok(ToDto(account));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCloudAccountRequest request)
    {
        var account = new CloudAccount
        {
            TenantId = _tenant.TenantId,
            Provider = request.Provider,
            Label = request.Label,
            EncryptedCredentials = _crypto.Encrypt(request.Credentials)
        };

        _db.CloudAccounts.Add(account);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = account.Id }, ToDto(account));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCloudAccountRequest request)
    {
        var account = await _db.CloudAccounts.FindAsync(id);
        if (account is null)
            return NotFound(new ErrorResponse("not_found", "Cloud account not found.", 404));

        if (request.Label is not null)
            account.Label = request.Label;

        if (request.Credentials is not null)
            account.EncryptedCredentials = _crypto.Encrypt(request.Credentials);

        await _db.SaveChangesAsync();
        return Ok(ToDto(account));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var account = await _db.CloudAccounts.FindAsync(id);
        if (account is null)
            return NotFound(new ErrorResponse("not_found", "Cloud account not found.", 404));

        _db.CloudAccounts.Remove(account);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static CloudAccountResponse ToDto(CloudAccount c) =>
        new(c.Id, c.Provider.ToString(), c.Label, c.CreatedAt);
}
