using System.Diagnostics;
using System.Text.Json;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Aura.Worker.Executors;
using Aura.Worker.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

// Aliases for clarity
using ExecutionMode = Aura.Core.Enums.ExecutionMode;

namespace Aura.Worker.Services;

public class RunWorkerService : BackgroundService
{
    private static readonly ActivitySource WorkerSource = new("Aura.Worker");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunWorkerService> _logger;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _pollIntervalSeconds;
    private readonly int _staleThresholdSeconds;

    private static readonly Counter RunsCompleted = Metrics.CreateCounter(
        "aura_runs_completed_total", "Completed runs by status",
        new CounterConfiguration { LabelNames = new[] { "status" } });
    private static readonly Counter LayersExecuted = Metrics.CreateCounter(
        "aura_layers_executed_total", "Executed layers by type and status",
        new CounterConfiguration { LabelNames = new[] { "executor_type", "status" } });
    private static readonly Histogram RunDuration = Metrics.CreateHistogram(
        "aura_run_duration_seconds", "Run duration",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(1, 2, 12) });
    private static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "aura_queue_depth", "Queued runs count");
    private static readonly Counter StaleReaped = Metrics.CreateCounter(
        "aura_stale_runs_reaped_total", "Stale runs reaped");
    private static readonly Histogram LayerDuration = Metrics.CreateHistogram(
        "aura_layer_duration_seconds", "Per-layer execution duration",
        new HistogramConfiguration
        {
            LabelNames = new[] { "executor_type", "layer_name", "operation_type" },
            Buckets = Histogram.ExponentialBuckets(1, 2, 12)
        });
    private static readonly Histogram OperationTypeDuration = Metrics.CreateHistogram(
        "aura_operation_type_duration_seconds", "Duration by operation type",
        new HistogramConfiguration
        {
            LabelNames = new[] { "operation_type" },
            Buckets = Histogram.ExponentialBuckets(1, 2, 12)
        });

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

        QueueDepth.Set(queuedRuns.Count);

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
        var logStream = scope.ServiceProvider.GetRequiredService<ILogStreamService>();

        var run = await db.DeploymentRuns
            .IgnoreQueryFilters()
            .Include(r => r.Layers.OrderBy(l => l.SortOrder))
            .Include(r => r.Deployment)
                .ThenInclude(d => d.Essence)
                    .ThenInclude(e => e.CloudAccount)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

        if (run is null || run.Status != RunStatus.Queued)
            return;

        // Restore trace context from the API-side trace that created this run
        ActivityContext parentContext = default;
        if (!string.IsNullOrEmpty(run.TraceParent))
            ActivityContext.TryParse(run.TraceParent, null, out parentContext);

        using var activity = WorkerSource.StartActivity("ProcessRun", ActivityKind.Consumer, parentContext);
        activity?.SetTag("deployment.run_id", runId.ToString());
        activity?.SetTag("deployment.id", run.DeploymentId.ToString());
        activity?.SetTag("deployment.layer_count", run.Layers.Count);

        // Claim the run
        run.Status = RunStatus.Running;
        run.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        var runStopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Processing run {RunId} with {LayerCount} layers", runId, run.Layers.Count);
        await PublishLogSafe(logStream, runId,
            $"Run started with {run.Layers.Count} layer(s)", ct);

        // Decrypt BYOS credentials if cloud account is linked
        var envVars = await BuildEnvVarsAsync(db, crypto, run, ct);

        // Execute layers in topological order
        var failed = false;
        foreach (var layer in run.Layers.OrderBy(l => l.SortOrder))
        {
            if (failed)
            {
                layer.Status = LayerStatus.Skipped;
                layer.CompletedAt = DateTime.UtcNow;
                await PublishLogSafe(logStream, runId,
                    $"[{layer.LayerName}] Skipped (previous layer failed)", ct);
                continue;
            }

            layer.Status = LayerStatus.Running;
            layer.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await PublishLogSafe(logStream, runId,
                $"[{layer.LayerName}] Running ({layer.ExecutorType})", ct);

            var modeStrategy = scope.ServiceProvider.GetRequiredService<IExecutionModeStrategy>();
            var executionMode = modeStrategy.Resolve(run, layer);
            var effectiveExecutorType = executionMode == ExecutionMode.EmissionLoadContainer
                ? ExecutorType.EmissionLoad
                : layer.ExecutorType;
            var executor = ResolveExecutor(scope.ServiceProvider, effectiveExecutorType);

            var opType = layer.Parameters?.Contains("operationType") == true
                ? ExtractOperationType(layer.Parameters) : "";
            using var layerActivity = WorkerSource.StartActivity($"ExecuteLayer:{layer.LayerName}");
            layerActivity?.SetTag("layer.name", layer.LayerName);
            layerActivity?.SetTag("layer.executor_type", effectiveExecutorType.ToString());
            layerActivity?.SetTag("layer.operation_type", opType);

            var layerStopwatch = Stopwatch.StartNew();
            try
            {
                // Use a temp work directory for this run
                var workDir = Path.Combine(Path.GetTempPath(), "aura-runs", runId.ToString());
                Directory.CreateDirectory(workDir);

                var result = await executor.ExecuteAsync(layer, workDir, envVars, ct);

                layer.Output = TruncateOutput(result.Output);
                layer.Status = result.Success ? LayerStatus.Succeeded : LayerStatus.Failed;
                layer.CompletedAt = DateTime.UtcNow;
                layerActivity?.SetTag("layer.status", layer.Status.ToString());
                if (!result.Success)
                    layerActivity?.SetStatus(ActivityStatusCode.Error, result.Output?[..Math.Min(200, result.Output?.Length ?? 0)]);
                LayersExecuted.WithLabels(layer.ExecutorType.ToString(), layer.Status.ToString()).Inc();

                // Observe per-layer and per-operation-type duration
                layerStopwatch.Stop();
                LayerDuration.WithLabels(
                    effectiveExecutorType.ToString(), layer.LayerName, opType)
                    .Observe(layerStopwatch.Elapsed.TotalSeconds);
                if (!string.IsNullOrEmpty(opType))
                    OperationTypeDuration.WithLabels(opType)
                        .Observe(layerStopwatch.Elapsed.TotalSeconds);

                // Stream output lines to subscribers
                if (!string.IsNullOrEmpty(result.Output))
                {
                    foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        await PublishLogSafe(logStream, runId,
                            $"[{layer.LayerName}] {line}", ct);
                }

                if (!result.Success)
                {
                    failed = true;
                    _logger.LogWarning("Layer {Layer} failed in run {RunId}", layer.LayerName, runId);
                    await PublishLogSafe(logStream, runId,
                        $"[{layer.LayerName}] FAILED", ct);
                }
                else
                {
                    _logger.LogInformation("Layer {Layer} succeeded in run {RunId}", layer.LayerName, runId);
                    await PublishLogSafe(logStream, runId,
                        $"[{layer.LayerName}] Succeeded", ct);
                }
            }
            catch (Exception ex)
            {
                layerStopwatch.Stop();
                LayerDuration.WithLabels(
                    effectiveExecutorType.ToString(), layer.LayerName, opType)
                    .Observe(layerStopwatch.Elapsed.TotalSeconds);
                if (!string.IsNullOrEmpty(opType))
                    OperationTypeDuration.WithLabels(opType)
                        .Observe(layerStopwatch.Elapsed.TotalSeconds);

                layer.Output = TruncateOutput(ex.Message);
                layer.Status = LayerStatus.Failed;
                layer.CompletedAt = DateTime.UtcNow;
                layerActivity?.SetTag("layer.status", "Failed");
                layerActivity?.SetStatus(ActivityStatusCode.Error, ex.Message[..Math.Min(200, ex.Message.Length)]);
                failed = true;
                _logger.LogError(ex, "Layer {Layer} threw in run {RunId}", layer.LayerName, runId);
                await PublishLogSafe(logStream, runId,
                    $"[{layer.LayerName}] Exception: {ex.Message}", ct);
            }

            await db.SaveChangesAsync(ct);
        }

        run.Status = failed ? RunStatus.Failed : RunStatus.Succeeded;
        run.CompletedAt = DateTime.UtcNow;
        activity?.SetTag("deployment.status", run.Status.ToString());
        if (failed)
            activity?.SetStatus(ActivityStatusCode.Error, "One or more layers failed");

        // Materialize cost estimate on the run for fast dashboard aggregation
        run.EstimatedCostUsd = ComputeRunCost(run);

        await db.SaveChangesAsync(ct);

        runStopwatch.Stop();
        RunDuration.Observe(runStopwatch.Elapsed.TotalSeconds);
        RunsCompleted.WithLabels(run.Status.ToString()).Inc();

        // Track experiment metrics for completed runs
        try
        {
            var experimentService = scope.ServiceProvider.GetRequiredService<IExperimentService>();
            var activeExperiments = await experimentService.GetActiveAsync("aura", ct);
            foreach (var experiment in activeExperiments)
            {
                var subjectKey = runId.ToString();
                var variantId = await experimentService.AssignVariantAsync(experiment.Id, subjectKey, ct);
                var subjectHash = Infrastructure.Services.ExperimentService.ComputeHash($"{experiment.Id}:{subjectKey}");
                await experimentService.TrackEventAsync(
                    experiment.Id, variantId, subjectHash,
                    "execution_time_seconds", runStopwatch.Elapsed.TotalSeconds,
                    JsonSerializer.Serialize(new { runId, status = run.Status.ToString() }), ct);
                _logger.LogInformation("Tracked experiment {ExperimentId} for run {RunId}: variant={Variant}, time={Time}s",
                    experiment.Id, runId, variantId, runStopwatch.Elapsed.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to track experiment metrics for run {RunId}", runId);
        }

        _logger.LogInformation("Run {RunId} completed with status {Status} in {ElapsedMs}ms",
            runId, run.Status, runStopwatch.ElapsedMilliseconds);
        await PublishLogSafe(logStream, runId,
            $"Run completed: {run.Status}", ct);

        // Signal stream end so SSE clients know to disconnect
        await PublishLogSafe(logStream, runId,
            Infrastructure.Services.RedisLogStreamService.StreamEndSentinel, ct);

        // Webhook callback
        if (!string.IsNullOrEmpty(run.Deployment?.WebhookUrl))
        {
            await webhookService.NotifyAsync(run.Deployment.WebhookUrl, run.Id, run.Status.ToString(), ct);
        }
    }

    /// <summary>
    /// Best-effort log publishing — never lets a Redis failure break run execution.
    /// </summary>
    private async Task PublishLogSafe(ILogStreamService logStream, Guid runId, string message, CancellationToken ct)
    {
        try
        {
            await logStream.PublishAsync(runId, message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish log for run {RunId}", runId);
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
        StaleReaped.Inc(staleRuns.Count);

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

            run.EstimatedCostUsd = ComputeRunCost(run);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<Dictionary<string, string>> BuildEnvVarsAsync(
        AuraDbContext db, ICryptoService crypto, DeploymentRun run, CancellationToken ct)
    {
        var envVars = new Dictionary<string, string>();

        try
        {
            using var doc = JsonDocument.Parse(run.SnapshotJson);
            if (doc.RootElement.TryGetProperty("baseEssence", out var baseEssence))
            {
                if (baseEssence.TryGetProperty("cloudProvider", out var provider))
                    envVars["AURA_CLOUD_PROVIDER"] = provider.GetString() ?? "";

                if (baseEssence.TryGetProperty("defaultRegion", out var region))
                    envVars["AURA_DEFAULT_REGION"] = region.GetString() ?? "";

                if (baseEssence.TryGetProperty("uniqueId", out var uniqueId))
                    envVars["AURA_UNIQUE_ID"] = uniqueId.GetString() ?? "";

                if (baseEssence.TryGetProperty("subscriptionId", out var subId))
                    envVars["AURA_SUBSCRIPTION_ID"] = subId.GetString() ?? "";
            }
        }
        catch
        {
            // Non-critical: snapshot may not have baseEssence
        }

        // Resolve BYOS credentials from the linked CloudAccount
        try
        {
            var cloudAccount = run.Deployment?.Essence?.CloudAccount;
            if (cloudAccount is null && run.Deployment?.Essence?.CloudAccountId is Guid accountId
                && accountId != Guid.Empty)
            {
                cloudAccount = await db.CloudAccounts.FindAsync(new object[] { accountId }, ct);
            }

            if (cloudAccount is not null && !string.IsNullOrEmpty(cloudAccount.EncryptedCredentials))
            {
                var decrypted = crypto.Decrypt(cloudAccount.EncryptedCredentials);
                var credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(decrypted)
                    ?? new Dictionary<string, string>();
                ByosResolver.PopulateEnvVars(envVars, credentials);
            }
        }
        catch
        {
            // Non-critical: cloud account may not exist or credentials may be invalid
        }

        return envVars;
    }

    private static ILayerExecutor ResolveExecutor(IServiceProvider sp, ExecutorType type) => type switch
    {
        ExecutorType.PowerShell => sp.GetRequiredService<PowerShellExecutor>(),
        ExecutorType.Python => sp.GetRequiredService<PythonExecutor>(),
        ExecutorType.CSharpSdk => sp.GetRequiredService<CSharpSdkExecutor>(),
        ExecutorType.Operation => sp.GetRequiredService<OperationExecutor>(),
        ExecutorType.EmissionLoad => sp.GetRequiredService<EmissionLoadExecutor>(),
        _ => throw new InvalidOperationException($"Unknown executor type: {type}")
    };

    private static decimal ComputeRunCost(DeploymentRun run)
    {
        var layerInputs = run.Layers.Select(l =>
        {
            var durationSeconds = 0m;
            if (l.StartedAt.HasValue && l.CompletedAt.HasValue)
                durationSeconds = (decimal)(l.CompletedAt.Value - l.StartedAt.Value).TotalSeconds;

            JsonElement? parameters = null;
            try
            {
                using var doc = JsonDocument.Parse(l.Parameters);
                parameters = doc.RootElement.Clone();
            }
            catch { }

            return new LayerCostInput(l.LayerName, l.OperationType, durationSeconds, parameters);
        }).ToList();

        return AzureCostEstimator.EstimateRunCost(layerInputs).TotalEstimatedCost;
    }

    private static string ExtractOperationType(string? parametersJson)
    {
        try
        {
            if (string.IsNullOrEmpty(parametersJson)) return "";
            using var doc = JsonDocument.Parse(parametersJson);
            return doc.RootElement.TryGetProperty("operationType", out var ot)
                ? ot.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static string TruncateOutput(string? output, int maxLength = 50_000)
    {
        if (string.IsNullOrEmpty(output)) return string.Empty;
        return output.Length > maxLength
            ? output[..maxLength] + "\n... [truncated]"
            : output;
    }
}
