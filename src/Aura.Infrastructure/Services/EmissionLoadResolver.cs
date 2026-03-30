using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Core.Models;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class EmissionLoadResolver : IEmissionLoadResolver
{
    private readonly AuraDbContext _db;
    private readonly ILogger<EmissionLoadResolver> _logger;

    public EmissionLoadResolver(AuraDbContext db, ILogger<EmissionLoadResolver> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<EmissionLoadConfig> ResolveAsync(
        Guid tenantId,
        string tenantSlug,
        CloudProvider provider,
        string baseLoadName,
        CancellationToken ct = default)
    {
        var providerName = provider.ToString().ToLowerInvariant();

        // Check if a customer-specific EmissionLoad image exists
        // Convention: EmissionLoad/{TenantSlug}/{provider}/ in the repo
        var customerImageName = $"aura/{tenantSlug}/emissionload-{providerName}";
        var defaultImageName = $"aura/emissionload-{providerName}";

        // Determine if tenant has a custom EmissionLoad definition
        // by checking if the directory exists at the well-known path
        var repoRoot = FindRepoRoot();
        var customerPath = repoRoot is not null
            ? Path.Combine(repoRoot, "EmissionLoad", tenantSlug, providerName)
            : null;

        string imageName;
        if (customerPath is not null && Directory.Exists(customerPath)
            && File.Exists(Path.Combine(customerPath, "Dockerfile")))
        {
            imageName = customerImageName;
            _logger.LogInformation(
                "Resolved customer-specific EmissionLoad image {Image} for tenant {TenantSlug} provider {Provider} baseLoad {BaseLoad}",
                imageName, tenantSlug, provider, baseLoadName);
        }
        else
        {
            imageName = defaultImageName;
            _logger.LogInformation(
                "Resolved default EmissionLoad image {Image} for tenant {TenantSlug} provider {Provider} baseLoad {BaseLoad}",
                imageName, tenantSlug, provider, baseLoadName);
        }

        return Task.FromResult(new EmissionLoadConfig(
            ImageName: imageName,
            ImageTag: "latest",
            Provider: provider,
            TenantSlug: tenantSlug));
    }

    /// <summary>
    /// Resolves the EmissionLoad config from a run's snapshot JSON by extracting
    /// the baseEssence metadata (cloudProvider, baseLoad) and looking up the tenant slug.
    /// </summary>
    public async Task<EmissionLoadConfig> ResolveFromSnapshotAsync(
        Guid tenantId, string snapshotJson, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {tenantId} not found.");

        var (provider, baseLoad) = ParseBaseEssence(snapshotJson);

        return await ResolveAsync(tenantId, tenant.Slug, provider, baseLoad, ct);
    }

    internal static (CloudProvider Provider, string BaseLoad) ParseBaseEssence(string snapshotJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(snapshotJson);
        var root = doc.RootElement;

        var baseLoad = "default";
        var providerStr = "azure";

        if (root.TryGetProperty("baseEssence", out var baseEssence))
        {
            if (baseEssence.TryGetProperty("baseLoad", out var bl))
                baseLoad = bl.GetString() ?? "default";

            if (baseEssence.TryGetProperty("cloudProvider", out var cp))
                providerStr = cp.GetString() ?? "azure";
        }

        var provider = providerStr.ToLowerInvariant() switch
        {
            "azure" => CloudProvider.Azure,
            "aws" => CloudProvider.Aws,
            "gcp" => CloudProvider.Gcp,
            _ => CloudProvider.Azure
        };

        return (provider, baseLoad);
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (dir is null) break;
            if (Directory.Exists(Path.Combine(dir, "EmissionLoad")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Fallback: check common deployment paths
        var candidates = new[] { "/app", "/home/rogerclaude/aura-platform" };
        foreach (var candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, "EmissionLoad")))
                return candidate;
        }

        return null;
    }
}
