using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Core.Services;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

namespace Aura.Worker.Services;

/// <summary>
/// Polls enabled deployments with cron expressions and enqueues runs
/// when the current UTC minute matches. Per-minute dedup prevents
/// duplicate runs within the same calendar minute.
/// </summary>
public class DeploymentSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeploymentSchedulerService> _logger;
    private readonly int _pollIntervalSeconds;

    private static readonly Counter ScheduledRuns = Metrics.CreateCounter(
        "aura_scheduled_runs_total", "Cron-triggered runs created");
    private static readonly Gauge LastEvaluation = Metrics.CreateGauge(
        "aura_scheduler_last_evaluation_timestamp", "Unix timestamp of last cron evaluation");

    public DeploymentSchedulerService(
        IServiceScopeFactory scopeFactory,
        ILogger<DeploymentSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _pollIntervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("SCHEDULER_POLL_SECONDS"), out var val) ? val : 30;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DeploymentSchedulerService started, poll interval: {Interval}s",
            _pollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateDueDeploymentsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in DeploymentSchedulerService loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("DeploymentSchedulerService stopped");
    }

    internal async Task EvaluateDueDeploymentsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuraDbContext>();
        var orchestration = scope.ServiceProvider.GetRequiredService<IDeploymentOrchestrationService>();

        var now = DateTime.UtcNow;
        LastEvaluation.Set(new DateTimeOffset(now).ToUnixTimeSeconds());
        var minuteFloor = FloorToMinute(now);

        // Fetch all enabled deployments that have a cron expression
        var candidates = await db.Deployments
            .IgnoreQueryFilters()
            .Include(d => d.Essence)
            .Where(d => d.IsEnabled && d.CronExpression != null && d.CronExpression != "")
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return;

        var enqueued = 0;

        foreach (var deployment in candidates)
        {
            var cron = CronExpression.TryParse(deployment.CronExpression);
            if (cron is null)
            {
                _logger.LogWarning(
                    "Deployment {DeploymentId} has invalid cron expression: \"{Cron}\", skipping",
                    deployment.Id, deployment.CronExpression);
                continue;
            }

            if (!cron.Matches(minuteFloor))
                continue;

            // Per-minute dedup: check if a run was already created for this deployment
            // within the current calendar minute window
            var alreadyQueued = await db.DeploymentRuns
                .IgnoreQueryFilters()
                .AnyAsync(r =>
                    r.DeploymentId == deployment.Id
                    && r.CreatedAt >= minuteFloor
                    && r.CreatedAt < minuteFloor.AddMinutes(1),
                    ct);

            if (alreadyQueued)
            {
                _logger.LogDebug(
                    "Skipping deployment {DeploymentId}: run already exists for minute {Minute}",
                    deployment.Id, minuteFloor);
                continue;
            }

            try
            {
                var run = await orchestration.CreateRunAsync(deployment, ct);
                enqueued++;
                ScheduledRuns.Inc();
                _logger.LogInformation(
                    "Cron-triggered run {RunId} for deployment {DeploymentId} ({DeploymentName})",
                    run.Id, deployment.Id, deployment.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to create cron-triggered run for deployment {DeploymentId}",
                    deployment.Id);
            }
        }

        if (enqueued > 0)
            _logger.LogInformation("Scheduler enqueued {Count} cron-triggered runs", enqueued);
    }

    internal static DateTime FloorToMinute(DateTime utcTime)
    {
        return new DateTime(utcTime.Year, utcTime.Month, utcTime.Day,
            utcTime.Hour, utcTime.Minute, 0, DateTimeKind.Utc);
    }
}
