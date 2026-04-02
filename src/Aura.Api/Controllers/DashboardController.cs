using System.Text.Json;
using Aura.Core.DTOs;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Member,Operator")]
[Route("api/v1/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly AuraDbContext _db;

    public DashboardController(AuraDbContext db)
    {
        _db = db;
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var essenceCount = await _db.Essences.CountAsync();
        var deploymentCount = await _db.Deployments.CountAsync();
        var userCount = await _db.Users.CountAsync();
        var accountCount = await _db.CloudAccounts.CountAsync();
        var totalCost = await _db.DeploymentRuns
            .Where(r => r.EstimatedCostUsd != null)
            .SumAsync(r => r.EstimatedCostUsd ?? 0m);

        return Ok(new DashboardStatsResponse(
            essenceCount, deploymentCount, userCount, accountCount,
            Math.Round(totalCost, 2)));
    }

    [HttpGet("recent-runs")]
    public async Task<IActionResult> GetRecentRuns([FromQuery] int limit = 10)
    {
        if (limit < 1) limit = 1;
        if (limit > 50) limit = 50;

        var runs = await _db.DeploymentRuns
            .Include(r => r.Deployment)
            .Include(r => r.Layers)
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToListAsync();

        var result = runs.Select(r => new RecentRunResponse(
            r.Id,
            r.DeploymentId,
            r.Deployment?.Name ?? "-",
            r.Status.ToString(),
            r.Layers.Count,
            r.StartedAt,
            r.CompletedAt,
            r.CreatedAt,
            r.EstimatedCostUsd
        )).ToList();

        return Ok(result);
    }

    [HttpGet("recent-essences")]
    public async Task<IActionResult> GetRecentEssences([FromQuery] int limit = 5)
    {
        if (limit < 1) limit = 1;
        if (limit > 25) limit = 25;

        var essences = await _db.Essences
            .OrderByDescending(e => e.UpdatedAt)
            .Take(limit)
            .ToListAsync();

        var result = essences.Select(e =>
        {
            var provider = "-";
            var layerCount = 0;
            try
            {
                using var doc = JsonDocument.Parse(e.EssenceJson);
                if (doc.RootElement.TryGetProperty("baseEssence", out var be)
                    && be.TryGetProperty("cloudProvider", out var cp))
                    provider = cp.GetString() ?? "-";
                if (doc.RootElement.TryGetProperty("layers", out var layers))
                    layerCount = layers.EnumerateObject().Count();
            }
            catch { }

            return new RecentEssenceResponse(
                e.Id, e.Name, provider, layerCount, e.CurrentVersion, e.UpdatedAt);
        }).ToList();

        return Ok(result);
    }
}
