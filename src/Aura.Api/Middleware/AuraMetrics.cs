using Prometheus;

namespace Aura.Api.Middleware;

public static class AuraMetrics
{
    // API metrics
    public static readonly Counter HttpRequestsTotal = Metrics.CreateCounter(
        "aura_http_requests_total", "Total HTTP requests",
        new CounterConfiguration { LabelNames = new[] { "method", "endpoint", "status_code" } });

    // Deployment metrics
    public static readonly Counter RunsCreatedTotal = Metrics.CreateCounter(
        "aura_runs_created_total", "Total deployment runs created",
        new CounterConfiguration { LabelNames = new[] { "trigger" } });

    public static readonly Counter RunsCompletedTotal = Metrics.CreateCounter(
        "aura_runs_completed_total", "Total deployment runs completed",
        new CounterConfiguration { LabelNames = new[] { "status" } });

    public static readonly Counter LayersExecutedTotal = Metrics.CreateCounter(
        "aura_layers_executed_total", "Total layers executed",
        new CounterConfiguration { LabelNames = new[] { "executor_type", "status" } });

    public static readonly Histogram RunDurationSeconds = Metrics.CreateHistogram(
        "aura_run_duration_seconds", "Deployment run duration in seconds",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(1, 2, 12) });

    public static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "aura_queue_depth", "Number of runs currently queued");

    public static readonly Counter StaleRunsReapedTotal = Metrics.CreateCounter(
        "aura_stale_runs_reaped_total", "Total stale runs reaped");

    public static readonly Counter ScheduledRunsTotal = Metrics.CreateCounter(
        "aura_scheduled_runs_total", "Total runs created by cron scheduler");

    // Auth metrics
    public static readonly Counter AuthAttemptsTotal = Metrics.CreateCounter(
        "aura_auth_attempts_total", "Total authentication attempts",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    public static readonly Counter InvitesCreatedTotal = Metrics.CreateCounter(
        "aura_invites_created_total", "Total user invites created");
}
