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
[Route("api/v1/deployments")]
public class DeploymentsController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;

    public DeploymentsController(AuraDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        var query = _db.Deployments.OrderBy(d => d.CreatedAt);
        var total = await query.CountAsync();
        var items = await query.Skip(offset).Take(limit)
            .Select(d => ToDto(d)).ToListAsync();

        return Ok(new PaginatedResponse<DeploymentResponse>(items, total, offset, limit));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var deployment = await _db.Deployments.FindAsync(id);
        if (deployment is null)
            return NotFound(new ErrorResponse("not_found", "Deployment not found.", 404));

        return Ok(ToDto(deployment));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDeploymentRequest request)
    {
        var essenceExists = await _db.Essences.AnyAsync(e => e.Id == request.EssenceId);
        if (!essenceExists)
            return BadRequest(new ErrorResponse("bad_request", "Essence not found.", 400));

        var deployment = new Deployment
        {
            TenantId = _tenant.TenantId,
            EssenceId = request.EssenceId,
            Name = request.Name,
            CronExpression = request.CronExpression,
            WebhookUrl = request.WebhookUrl,
            IsEnabled = request.IsEnabled
        };

        _db.Deployments.Add(deployment);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = deployment.Id }, ToDto(deployment));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDeploymentRequest request)
    {
        var deployment = await _db.Deployments.FindAsync(id);
        if (deployment is null)
            return NotFound(new ErrorResponse("not_found", "Deployment not found.", 404));

        if (request.Name is not null)
            deployment.Name = request.Name;

        if (request.CronExpression is not null)
            deployment.CronExpression = request.CronExpression;

        if (request.WebhookUrl is not null)
            deployment.WebhookUrl = request.WebhookUrl;

        if (request.IsEnabled.HasValue)
            deployment.IsEnabled = request.IsEnabled.Value;

        await _db.SaveChangesAsync();
        return Ok(ToDto(deployment));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deployment = await _db.Deployments.FindAsync(id);
        if (deployment is null)
            return NotFound(new ErrorResponse("not_found", "Deployment not found.", 404));

        _db.Deployments.Remove(deployment);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static DeploymentResponse ToDto(Deployment d) =>
        new(d.Id, d.EssenceId, d.Name, d.CronExpression, d.WebhookUrl, d.IsEnabled, d.CreatedAt);
}
