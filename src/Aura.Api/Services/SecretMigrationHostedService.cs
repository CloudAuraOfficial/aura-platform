using Aura.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aura.Api.Services;

/// <summary>
/// Runs <see cref="SecretMigrationService"/> once at API startup. Registered only when
/// MIGRATE_SECRETS_TO_V2=true, so it is an explicit rollout step. Safe to run again (idempotent).
/// A failure is logged and does not stop the API.
/// </summary>
public sealed class SecretMigrationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecretMigrationHostedService> _logger;

    public SecretMigrationHostedService(IServiceScopeFactory scopeFactory, ILogger<SecretMigrationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Secret migration to v2 starting (MIGRATE_SECRETS_TO_V2=true)");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var migration = scope.ServiceProvider.GetRequiredService<SecretMigrationService>();
            await migration.MigrateAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Secret migration to v2 cancelled by shutdown");
        }
        catch (Exception ex)
        {
            // Type only. The message can carry database detail, and nothing here may carry a secret.
            _logger.LogError("Secret migration to v2 aborted: error={ErrorType}. Re-run after fixing the cause.",
                ex.GetType().Name);
        }
    }
}
