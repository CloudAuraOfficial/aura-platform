namespace Aura.Worker.Services;

public class DeploymentSchedulerService : BackgroundService
{
    private readonly ILogger<DeploymentSchedulerService> _logger;
    private readonly int _pollIntervalSeconds;

    public DeploymentSchedulerService(ILogger<DeploymentSchedulerService> logger)
    {
        _logger = logger;
        _pollIntervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("SCHEDULER_POLL_SECONDS"), out var val) ? val : 30;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeploymentSchedulerService started, poll interval: {Interval}s", _pollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // TODO: Poll for due cron deployments, enqueue runs
            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
        }
    }
}
