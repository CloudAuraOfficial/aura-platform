using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Worker.Reaper;
using Aura.Worker.Reaper.Providers;
using Aura.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Aura.Tests;

/// <summary>
/// #53: sweep behaviour with a mocked provider reaper and crypto. No cloud SDK is reached.
/// Covers the off-by-default gate, dry-run, real deletes of expired resources only, and per-account isolation.
/// </summary>
public class ResourceReaperServiceTests : IDisposable
{
    private const string PastExpiry = "20200101000000";
    private const string FutureExpiry = "20990101000000";

    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<ICryptoService> _crypto = new();
    private readonly Mock<IProviderResourceReaper> _azure = new();
    private readonly Guid _accountId = Guid.NewGuid();

    public ResourceReaperServiceTests()
    {
        var dbName = $"ReaperTest_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<AuraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<ITenantContext>(new WorkerTenantContext());
        services.AddSingleton(_crypto.Object);
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        _azure.SetupGet(r => r.Provider).Returns(CloudProvider.Azure);
        _crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Returns("""{ "tenantId": "t", "clientId": "c", "clientSecret": "s" }""");
        SeedAccount(CloudProvider.Azure, "enc-azure");
    }

    public void Dispose() => _serviceProvider.Dispose();

    private void SeedAccount(CloudProvider provider, string encrypted)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuraDbContext>();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}" });
        db.CloudAccounts.Add(new CloudAccount
        {
            Id = _accountId,
            TenantId = tenantId,
            Provider = provider,
            Label = "test",
            EncryptedCredentials = encrypted,
        });
        db.SaveChanges();
    }

    private ResourceReaperService Build(bool enabled, bool dryRun, params IProviderResourceReaper[] reapers) =>
        new(
            new ReaperOptions(enabled, dryRun, TimeSpan.FromMinutes(15)),
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            reapers,
            _serviceProvider.GetRequiredService<ILogger<ResourceReaperService>>());

    private static ReapableResource Vm(string id, string expiresAt, int rank = 10) =>
        new(CloudProvider.Azure, "Microsoft.Compute/virtualMachines", id, "eastus", rank, expiresAt);

    [Fact]
    public async Task Disabled_ExecuteReturnsWithoutListingOrDeleting()
    {
        var service = Build(enabled: false, dryRun: false, _azure.Object);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        _azure.Verify(r => r.ListTaggedAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _azure.Verify(r => r.DeleteAsync(It.IsAny<ReapableResource>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DryRun_ListsAndLogsButNeverDeletes()
    {
        _azure.Setup(r => r.ListTaggedAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Vm("vm-expired", PastExpiry), Vm("vm-live", FutureExpiry) });
        var service = Build(enabled: true, dryRun: true, _azure.Object);

        var summary = await service.ReapOnceAsync(CancellationToken.None);

        Assert.True(summary.DryRun);
        Assert.Equal(1, summary.Expired);
        Assert.Equal(1, summary.DryRunSkipped);
        Assert.Equal(0, summary.Deleted);
        _azure.Verify(r => r.DeleteAsync(It.IsAny<ReapableResource>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RealRun_DeletesExpiredOnly_InDeletionOrder()
    {
        _azure.Setup(r => r.ListTaggedAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                Vm("vnet-expired", PastExpiry, rank: 40),
                Vm("vm-live", FutureExpiry, rank: 10),
                Vm("vm-expired", PastExpiry, rank: 10),
                Vm("bad-tag", "not-a-date", rank: 10),
            });
        var deleted = new List<string>();
        _azure.Setup(r => r.DeleteAsync(It.IsAny<ReapableResource>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<ReapableResource, Dictionary<string, string>, CancellationToken>((r, _, _) => deleted.Add(r.Id))
            .Returns(Task.CompletedTask);
        var service = Build(enabled: true, dryRun: false, _azure.Object);

        var summary = await service.ReapOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "vm-expired", "vnet-expired" }, deleted);
        Assert.Equal(2, summary.Deleted);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public async Task RealRun_OneFailedDelete_DoesNotStopTheSweep()
    {
        _azure.Setup(r => r.ListTaggedAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Vm("first", PastExpiry), Vm("second", PastExpiry) });
        _azure.Setup(r => r.DeleteAsync(It.Is<ReapableResource>(x => x.Id == "first"), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _azure.Setup(r => r.DeleteAsync(It.Is<ReapableResource>(x => x.Id == "second"), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = Build(enabled: true, dryRun: false, _azure.Object);

        var summary = await service.ReapOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Deleted);
    }

    [Fact]
    public async Task UnreadableCredentials_SkipsAccountWithoutListing()
    {
        _crypto.Setup(c => c.Decrypt(It.IsAny<string>())).Throws(new InvalidOperationException("bad key"));
        var service = Build(enabled: true, dryRun: false, _azure.Object);

        var summary = await service.ReapOnceAsync(CancellationToken.None);

        Assert.Equal(0, summary.Accounts);
        _azure.Verify(r => r.ListTaggedAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AccountWithoutMatchingProviderReaper_IsIgnored()
    {
        var aws = new Mock<IProviderResourceReaper>();
        aws.SetupGet(r => r.Provider).Returns(CloudProvider.Aws);
        var service = Build(enabled: true, dryRun: false, aws.Object);

        var summary = await service.ReapOnceAsync(CancellationToken.None);

        Assert.Equal(0, summary.Accounts);
        aws.Verify(r => r.ListTaggedAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListingFailure_IsContainedToThatAccount()
    {
        _azure.Setup(r => r.ListTaggedAsync(It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));
        var service = Build(enabled: true, dryRun: false, _azure.Object);

        var summary = await service.ReapOnceAsync(CancellationToken.None);

        Assert.Equal(0, summary.Expired);
        Assert.Equal(0, summary.Deleted);
        _azure.Verify(r => r.DeleteAsync(It.IsAny<ReapableResource>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
