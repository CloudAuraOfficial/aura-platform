using System.Text.Json;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Worker.Operations;
using Aura.Worker.Reaper.Providers;
using Aura.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace Aura.Worker.Reaper;

/// <summary>
/// Opt-in sweeper for cloud resources whose aura-expires-at tag is in the past (#53). Runs only when
/// REAPER_ENABLED=true, and deletes only when REAPER_DRY_RUN=false. Otherwise it logs what it would delete.
/// Each sweep walks every stored CloudAccount, lists its tagged resources, and deletes the expired ones in
/// dependency order. Credentials are decrypted in memory and never logged.
/// </summary>
public sealed class ResourceReaperService : BackgroundService
{
    private static readonly Counter ReaperOutcomes = Metrics.CreateCounter(
        "aura_reaper_resources_total", "Expired tagged resources by reaper outcome",
        new CounterConfiguration { LabelNames = new[] { "outcome" } });

    private readonly ReaperOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<IProviderResourceReaper> _reapers;
    private readonly ILogger<ResourceReaperService> _logger;

    public ResourceReaperService(
        ReaperOptions options,
        IServiceScopeFactory scopeFactory,
        IEnumerable<IProviderResourceReaper> reapers,
        ILogger<ResourceReaperService> logger)
    {
        _options = options;
        _scopeFactory = scopeFactory;
        _reapers = reapers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Resource reaper disabled; set REAPER_ENABLED=true to enable");
            return;
        }

        _logger.LogInformation(
            "Resource reaper enabled: interval={IntervalSeconds}s dryRun={DryRun}",
            (int)_options.Interval.TotalSeconds, _options.DryRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReapOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resource reaper sweep failed");
            }

            await Task.Delay(_options.Interval, stoppingToken);
        }
    }

    /// <summary>One sweep over all stored cloud accounts. Internal so tests can drive it without the timer.</summary>
    internal async Task<ReaperSweepSummary> ReapOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuraDbContext>();
        var crypto = scope.ServiceProvider.GetRequiredService<ICryptoService>();
        var utcNow = DateTime.UtcNow;

        var accounts = await db.CloudAccounts.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.EncryptedCredentials != string.Empty)
            .ToListAsync(ct);

        var summary = new ReaperSweepSummary(_options.DryRun);
        var handled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();

            var reaper = _reapers.FirstOrDefault(r => r.Provider == account.Provider);
            if (reaper is null)
                continue;

            var envVars = TryLoadEnvVars(crypto, account);
            if (envVars is null)
                continue;

            summary.Accounts++;

            IReadOnlyList<ReapableResource> expired;
            try
            {
                var tagged = await reaper.ListTaggedAsync(envVars, ct);
                summary.Tagged += tagged.Count;
                expired = ResourceReaperPlanner.SelectExpired(tagged, utcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Reaper could not list tagged resources for account {AccountId} ({Provider})",
                    account.Id, account.Provider);
                continue;
            }

            _logger.LogInformation(
                "Reaper account {AccountId} ({Provider}): expired={ExpiredCount} dryRun={DryRun}",
                account.Id, account.Provider, expired.Count, _options.DryRun);

            foreach (var resource in expired)
            {
                // Two accounts can point at the same subscription or project. Delete each resource once.
                if (!handled.Add($"{resource.Provider}:{resource.Id}"))
                    continue;

                summary.Expired++;

                if (_options.DryRun)
                {
                    summary.DryRunSkipped++;
                    ReaperOutcomes.WithLabels("dry_run").Inc();
                    _logger.LogInformation(
                        "[dry-run] Would delete {Kind} {ResourceId} (expired {ExpiresAt}) in account {AccountId}",
                        resource.Kind, resource.Id, resource.ExpiresAtTag, account.Id);
                    continue;
                }

                try
                {
                    await reaper.DeleteAsync(resource, envVars, ct);
                    summary.Deleted++;
                    ReaperOutcomes.WithLabels("deleted").Inc();
                    _logger.LogInformation(
                        "Deleted expired {Kind} {ResourceId} in account {AccountId}",
                        resource.Kind, resource.Id, account.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    summary.Failed++;
                    ReaperOutcomes.WithLabels("failed").Inc();
                    _logger.LogWarning(ex,
                        "Failed to delete expired {Kind} {ResourceId} in account {AccountId}",
                        resource.Kind, resource.Id, account.Id);
                }
            }
        }

        _logger.LogInformation(
            "Reaper sweep finished: accounts={Accounts} tagged={Tagged} expired={Expired} deleted={Deleted} failed={Failed} dryRunSkipped={DryRunSkipped}",
            summary.Accounts, summary.Tagged, summary.Expired, summary.Deleted, summary.Failed, summary.DryRunSkipped);

        return summary;
    }

    /// <summary>
    /// Decrypts one account's credentials into a provider env-var map. Returns null (and logs the account id,
    /// never the secret or the parse error text) when they cannot be read, so one bad account cannot stop a sweep.
    /// </summary>
    private Dictionary<string, string>? TryLoadEnvVars(ICryptoService crypto, CloudAccount account)
    {
        try
        {
            var json = RunWorkerService.DecryptCredentials(crypto, account);
            var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            var envVars = new Dictionary<string, string>();
            ByosResolver.PopulateEnvVars(envVars, creds);
            return envVars;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Reaper skipped account {AccountId} ({Provider}): credentials unreadable ({ErrorType})",
                account.Id, account.Provider, ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>Counts from one sweep. Logged at the end of every sweep and returned for tests.</summary>
public sealed class ReaperSweepSummary(bool dryRun)
{
    public bool DryRun { get; } = dryRun;
    public int Accounts { get; internal set; }
    public int Tagged { get; internal set; }
    public int Expired { get; internal set; }
    public int Deleted { get; internal set; }
    public int Failed { get; internal set; }
    public int DryRunSkipped { get; internal set; }
}
