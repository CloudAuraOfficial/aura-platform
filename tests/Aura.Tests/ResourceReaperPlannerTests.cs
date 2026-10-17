using Aura.Core.Enums;
using Aura.Worker.Operations.Common;
using Aura.Worker.Reaper;
using Xunit;

namespace Aura.Tests;

/// <summary>#53: selection and ordering for the reaper. Pure logic, no cloud.</summary>
public class ResourceReaperPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);

    private static ReapableResource Resource(string id, int rank, string expiresAt) =>
        new(CloudProvider.Azure, "Microsoft.Compute/virtualMachines", id, "eastus", rank, expiresAt);

    [Fact]
    public void SelectExpired_KeepsOnlyResourcesPastExpiry()
    {
        var candidates = new[]
        {
            Resource("expired", 10, "20261011095959"),
            Resource("future", 10, "20261011100001"),
        };

        var selected = ResourceReaperPlanner.SelectExpired(candidates, Now);

        Assert.Equal(new[] { "expired" }, selected.Select(r => r.Id));
    }

    [Fact]
    public void SelectExpired_ExpiryExactlyNow_IsExpired()
    {
        var selected = ResourceReaperPlanner.SelectExpired(
            new[] { Resource("boundary", 10, ResourceTags.FormatExpiry(Now)) }, Now);

        Assert.Single(selected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("2020-01-01T00:00:00Z")]
    public void SelectExpired_MalformedExpiry_IsNeverSelected(string expiresAt)
    {
        var selected = ResourceReaperPlanner.SelectExpired(new[] { Resource("bad-tag", 10, expiresAt) }, Now);

        Assert.Empty(selected);
    }

    [Fact]
    public void SelectExpired_OrdersByDeletionRankThenId()
    {
        var past = "20200101000000";
        var candidates = new[]
        {
            Resource("vnet", 40, past),
            Resource("nic-b", 20, past),
            Resource("vm", 10, past),
            Resource("nic-a", 20, past),
        };

        var selected = ResourceReaperPlanner.SelectExpired(candidates, Now);

        Assert.Equal(new[] { "vm", "nic-a", "nic-b", "vnet" }, selected.Select(r => r.Id));
    }

    [Fact]
    public void SelectExpired_DuplicateResource_SelectedOnce()
    {
        var past = "20200101000000";
        var selected = ResourceReaperPlanner.SelectExpired(
            new[] { Resource("same", 10, past), Resource("same", 10, past) }, Now);

        Assert.Single(selected);
    }

    [Fact]
    public void SelectExpired_SameIdDifferentProvider_BothKept()
    {
        var past = "20200101000000";
        var azure = Resource("shared-id", 10, past);
        var aws = azure with { Provider = CloudProvider.Aws };

        var selected = ResourceReaperPlanner.SelectExpired(new[] { azure, aws }, Now);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SelectExpired_NoCandidates_ReturnsEmpty()
    {
        Assert.Empty(ResourceReaperPlanner.SelectExpired(Array.Empty<ReapableResource>(), Now));
    }
}
