using System.Text.Json;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Aura.Worker.Executors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aura.Worker.Services;

public class RunWorkerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunWorkerService> _logger;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _pollIntervalSeconds;
    private readonly int _staleThresholdSeconds;

    public RunWorkerService(IServiceScopeFactory scopeFactory, ILogger<RunWorkerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var concurrency = int.TryParse(
            Environment.GetEnvironmentVariable("WORKER_CONCURRENCY"), out var c) ? c : 5;
        _semaphore = new SemaphoreSlim(concurrency, concurrency);

        _pollIntervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("SCHEDULER_POLL_SECONDS"), out var p) ? p : 10;

        _staleThresholdSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("RUN_STALE_THRESHOLD_SECONDS"), out var s) ? s : 7200;

        _logger.LogInformation(
            "RunWorkerService configured: concurrency={Concurrency}, poll={Poll}s, staleThreshold={Stale}s",
            concurrency, _pollIntervalSeconds, _staleThresholdSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RunWorkerService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReapStaleRunsAsync(stoppingToken);
                await DequeueAndProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RunWorkerService loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
        }
    }

    private async Task DequeueAndProcessAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuraDbContext>();

        // FIFO: oldest Queued runs first
        var queuedRuns = await db.DeploymentRuns
            .IgnoreQueryFilters()
            .Where(r => r.Status == RunStatus.Queued)
            .OrderBy(r => r.CreatedAt)
            .Take(10)
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (queuedRuns.Count == 0)
            return;

        _logger.LogInformation("Found {Count} queued runs", queuedRuns.Count);

        var tasks = queuedRuns.Select(runId => ProcessRunWithSemaphoreAsync(runId, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessRunWithSemaphoreAsync(Guid runId, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await ProcessRunAsync(runId, ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task ProcessRunAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuraDbContext>();
        var crypto = scope.ServiceProvider.GetRequiredService<ICryptoService>();
        var webhookService = scope.ServiceProvider.GetRequiredService<WebhookService>();

        var run = await db.DeploymentRuns
            .IgnoreQueryFilters()
            .Include(r => r.Layers.OrderBy(l => l.SortOrder))
            .Include(r => r.Deployment)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

        if (run is null || run.Status != RunStatus.Queued)
            return;

        // Claim the run
        run.Status = RunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Processing run {RunId} with {LayerCount} layers", runId, run.Layers.Count);

        // Decrypt BYOS credentials if cloud account is linked
        var envVars = await BuildEnvVarsAsync(db, crypto, run.SnapshotJson, ct);

        // Execute layers in topological order
        var failed = false;
        foreach (var layer in run.Layers.OrderBy(l => l.SortOrder))
        {
            if (failed)
            {
                layer.Status = LayerStatus.Skipped;
                layer.CompletedAt = DateTime.UtcNow;
                continue;
            }

            layer.Status = LayerStatus.Running;
            layer.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var executor = ResolveExecutor(scope.ServiceProvider, layer.ExecutorType);
            try
            {
                // Use a temp work directory for this run
                var workDir = Path.Combine(Path.GetTempPath(), "aura-runs", runId.ToString());
                Directory.CreateDirectory(workDir);

                var result = await executor.ExecuteAsync(layer, workDir, envVars, ct);

                layer.Output = TruncateOutput(result.Output);
                layer.Status = result.Success ? LayerStatus.Succeeded : LayerStatus.Failed;
                layer.CompletedAt = DateTime.UtcNow;

                if (!result.Success)
                {
                    failed = true;
                    _logger.LogWarning("Layer {Layer} failed in run {RunId}", layer.LayerName, runId);
                }
                else
                {
                    _logger.LogInformation("Layer {Layer} succeeded in run {RunId}", layer.LayerName, runId);
                }
            }
            catch (Exception ex)
            {
                layer.Output = TruncateOutput(ex.Message);
                layer.Status = LayerStatus.Failed;
                layer.CompletedAt = DateTime.UtcNow;
                failed = true;
                _logger.LogError(ex, "Layer {Layer} threw in run {RunId}", layer.LayerName, runId);
            }

            await db.SaveChangesAsync(ct);
        }

        run.Status = failed ? RunStatus.Failed : RunStatus.Succeeded;
        run.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Run {RunId} completed with status {Status}", runId, run.Status);

        // Webhook callback
        if (!string.IsNullOrEmpty(run.Deployment?.WebhookUrl))
        {
            await webhookService.NotifyAsync(run.Deployment.WebhookUrl, run.Id, run.Status.ToString(), ct);
        }
    }

    private async Task ReapStaleRunsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuraDbContext>();

        var threshold = DateTime.UtcNow.AddSeconds(-_staleThresholdSeconds);
        var staleRuns = await db.DeploymentRuns
            .IgnoreQueryFilters()
            .Where(r => r.Status == RunStatus.Running && r.StartedAt < threshold)
            .Include(r => r.Layers)
            .ToListAsync(ct);

        if (staleRuns.Count == 0)
            return;

        _logger.LogWarning("Reaping {Count} stale runs", staleRuns.Count);

        foreach (var run in staleRuns)
        {
            run.Status = RunStatus.Failed;
            run.CompletedAt = DateTime.UtcNow;

            foreach (var layer in run.Layers.Where(l =>
                l.Status == LayerStatus.Pending || l.Status == LayerStatus.Running))
            {
                layer.Status = LayerStatus.Failed;
                layer.Output = "Reaped: run exceeded stale threshold.";
                layer.CompletedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static Task<Dictionary<string, string>> BuildEnvVarsAsync(
        AuraDbContext db, ICryptoService crypto, string snapshotJson, CancellationToken ct)
    {
        var envVars = new Dictionary<string, string>();

        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (doc.RootElement.TryGetProperty("baseEssence", out var baseEssence))
            {
                if (baseEssence.TryGetProperty("cloudProvider", out var provider))
                    envVars["AURA_CLOUD_PROVIDER"] = provider.GetString() ?? "";

                if (baseEssence.TryGetProperty("defaultRegion", out var region))
                    envVars["AURA_DEFAULT_REGION"] = region.GetString() ?? "";

                if (baseEssence.TryGetProperty("uniqueId", out var uniqueId))
                    envVars["AURA_UNIQUE_ID"] = uniqueId.GetString() ?? "";

                // Look up cloud account credentials
                if (baseEssence.TryGetProperty("subscriptionId", out var subId))
                    envVars["AURA_SUBSCRIPTION_ID"] = subId.GetString() ?? "";
            }
        }
        catch
        {
            // Non-critical: snapshot may not have baseEssence
        }

        return Task.FromResult(envVars);
    }

    private static ILayerExecutor ResolveExecutor(IServiceProvider sp, ExecutorType type) => type switch
    {
        ExecutorType.PowerShell => sp.GetRequiredService<PowerShellExecutor>(),
        ExecutorType.Python => sp.GetRequiredService<PythonExecutor>(),
        ExecutorType.CSharpSdk => sp.GetRequiredService<CSharpSdkExecutor>(),
        _ => throw new InvalidOperationException($"Unknown executor type: {type}")
    };

    private static string TruncateOutput(string? output, int maxLength = 50_000)
    {
        if (string.IsNullOrEmpty(output)) return string.Empty;
        return output.Length > maxLength
            ? output[..maxLength] + "\n... [truncated]"
            : output;
    }
}
