using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Member,Operator")]
[Route("api/v1/deployments")]
public class DeploymentsController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IDeploymentOrchestrationService _orchestration;

    public DeploymentsController(
        AuraDbContext db, ITenantContext tenant, IDeploymentOrchestrationService orchestration)
    {
        _db = db;
        _tenant = tenant;
        _orchestration = orchestration;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        (offset, limit) = PaginationDefaults.Clamp(offset, limit);
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
    [Authorize(Roles = "Admin,Member")]
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
    [Authorize(Roles = "Admin,Member")]
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
    [Authorize(Roles = "Admin,Member")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deployment = await _db.Deployments.FindAsync(id);
        if (deployment is null)
            return NotFound(new ErrorResponse("not_found", "Deployment not found.", 404));

        _db.Deployments.Remove(deployment);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/runs")]
    public async Task<IActionResult> CreateRun(Guid id)
    {
        var deployment = await _db.Deployments.FindAsync(id);
        if (deployment is null)
            return NotFound(new ErrorResponse("not_found", "Deployment not found.", 404));

        if (!deployment.IsEnabled)
            return BadRequest(new ErrorResponse("bad_request", "Deployment is disabled.", 400));

        var run = await _orchestration.CreateRunAsync(deployment);
        return CreatedAtAction(nameof(GetRun), new { id, runId = run.Id }, ToRunDto(run));
    }

    [HttpGet("{id:guid}/runs")]
    public async Task<IActionResult> ListRuns(
        Guid id, [FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        (offset, limit) = PaginationDefaults.Clamp(offset, limit);
        var exists = await _db.Deployments.AnyAsync(d => d.Id == id);
        if (!exists)
            return NotFound(new ErrorResponse("not_found", "Deployment not found.", 404));

        var query = _db.DeploymentRuns
            .Where(r => r.DeploymentId == id)
            .OrderByDescending(r => r.CreatedAt);

        var total = await query.CountAsync();
        var items = await query.Skip(offset).Take(limit)
            .Include(r => r.Layers.OrderBy(l => l.SortOrder))
            .ToListAsync();

        return Ok(new PaginatedResponse<DeploymentRunResponse>(
            items.Select(ToRunDto).ToList(), total, offset, limit));
    }

    [HttpGet("{id:guid}/runs/{runId:guid}")]
    public async Task<IActionResult> GetRun(Guid id, Guid runId)
    {
        var run = await _db.DeploymentRuns
            .Include(r => r.Layers.OrderBy(l => l.SortOrder))
            .FirstOrDefaultAsync(r => r.Id == runId && r.DeploymentId == id);

        if (run is null)
            return NotFound(new ErrorResponse("not_found", "Run not found.", 404));

        return Ok(ToRunDto(run));
    }

    [HttpPost("{id:guid}/runs/{runId:guid}/cancel")]
    public async Task<IActionResult> CancelRun(Guid id, Guid runId)
    {
        var run = await _db.DeploymentRuns
            .Include(r => r.Layers)
            .FirstOrDefaultAsync(r => r.Id == runId && r.DeploymentId == id);

        if (run is null)
            return NotFound(new ErrorResponse("not_found", "Run not found.", 404));

        if (run.Status != RunStatus.Queued && run.Status != RunStatus.Running)
            return BadRequest(new ErrorResponse("bad_request",
                $"Cannot cancel a run with status '{run.Status}'.", 400));

        run.Status = RunStatus.Cancelled;
        run.CompletedAt = DateTime.UtcNow;

        foreach (var layer in run.Layers.Where(l =>
            l.Status == LayerStatus.Pending || l.Status == LayerStatus.Running))
        {
            layer.Status = LayerStatus.Skipped;
            layer.Output = "Cancelled by user.";
            layer.CompletedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Ok(ToRunDto(run));
    }

    [HttpGet("{id:guid}/runs/{runId:guid}/layers")]
    public async Task<IActionResult> ListLayers(Guid id, Guid runId)
    {
        var run = await _db.DeploymentRuns.AnyAsync(r => r.Id == runId && r.DeploymentId == id);
        if (!run)
            return NotFound(new ErrorResponse("not_found", "Run not found.", 404));

        var layers = await _db.DeploymentLayers
            .Where(l => l.RunId == runId)
            .OrderBy(l => l.SortOrder)
            .ToListAsync();

        return Ok(layers.Select(ToLayerDto).ToList());
    }

    private static DeploymentResponse ToDto(Deployment d) =>
        new(d.Id, d.EssenceId, d.Name, d.CronExpression, d.WebhookUrl, d.IsEnabled, d.CreatedAt);

    private static DeploymentRunResponse ToRunDto(DeploymentRun r) =>
        new(r.Id, r.DeploymentId, r.Status.ToString(), r.SnapshotJson,
            r.Layers.OrderBy(l => l.SortOrder).Select(ToLayerDto).ToList(),
            r.StartedAt, r.CompletedAt, r.CreatedAt);

    private static DeploymentLayerResponse ToLayerDto(DeploymentLayer l) =>
        new(l.Id, l.LayerName, l.ExecutorType.ToString(), l.Status.ToString(),
            l.Parameters, l.ScriptPath, l.DependsOn, l.SortOrder,
            l.Output, l.StartedAt, l.CompletedAt);
}
