using System.Text.Json;
using Aura.Core.Entities;
using Aura.Worker.Operations.Common;
using Aura.Worker.Services;
using Xunit;

namespace Aura.Tests;

/// <summary>#53: expiry tags applied to created resources.</summary>
public class ResourceTagsTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, string> RunEnv = new()
    {
        [ResourceTags.RunIdEnvVar] = "7d1c1f2e-0000-4000-8000-000000000001",
        [ResourceTags.TenantIdEnvVar] = "7d1c1f2e-0000-4000-8000-000000000002",
    };

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void TryBuild_NoOverride_ExpiresTwoHoursAfterNow()
    {
        Assert.True(ResourceTags.TryBuild(RunEnv, Json("{}"), Now, out var tags, out var error));

        Assert.Null(error);
        Assert.Equal("20261011120000", tags[ResourceTags.ExpiresAtKey]);
    }

    [Fact]
    public void TryBuild_IncludesRunAndTenantIds()
    {
        ResourceTags.TryBuild(RunEnv, Json("{}"), Now, out var tags, out _);

        Assert.Equal(RunEnv[ResourceTags.RunIdEnvVar], tags[ResourceTags.RunIdKey]);
        Assert.Equal(RunEnv[ResourceTags.TenantIdEnvVar], tags[ResourceTags.TenantIdKey]);
    }

    [Fact]
    public void TryBuild_NoRunContext_OmitsIdTagsButStillSetsExpiry()
    {
        ResourceTags.TryBuild(new Dictionary<string, string>(), Json("{}"), Now, out var tags, out _);

        Assert.False(tags.ContainsKey(ResourceTags.RunIdKey));
        Assert.False(tags.ContainsKey(ResourceTags.TenantIdKey));
        Assert.True(tags.ContainsKey(ResourceTags.ExpiresAtKey));
    }

    [Fact]
    public void TryBuild_ExpiresInMinutesOverride_IsApplied()
    {
        ResourceTags.TryBuild(RunEnv, Json("""{ "expiresInMinutes": 30 }"""), Now, out var tags, out var error);

        Assert.Null(error);
        Assert.Equal("20261011103000", tags[ResourceTags.ExpiresAtKey]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10081")]
    [InlineData("\"soon\"")]
    [InlineData("1.5")]
    public void TryBuild_InvalidExpiresInMinutes_ReturnsError(string value)
    {
        var ok = ResourceTags.TryBuild(RunEnv, Json($$"""{ "expiresInMinutes": {{value}} }"""), Now, out _, out var error);

        Assert.False(ok);
        Assert.Contains("expiresInMinutes", error);
    }

    [Fact]
    public void FormatAndParse_RoundTrip_AndAreLowercaseSafe()
    {
        var formatted = ResourceTags.FormatExpiry(Now);

        Assert.Matches("^[0-9]{14}$", formatted);
        Assert.True(ResourceTags.TryParseExpiry(formatted, out var parsed));
        Assert.Equal(Now, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-10-11T10:00:00Z")]
    [InlineData("20261311100000")]
    public void TryParseExpiry_MalformedValue_ReturnsFalse(string? value)
    {
        Assert.False(ResourceTags.TryParseExpiry(value, out _));
    }

    [Fact]
    public void ApplyTo_CopiesEveryTagIntoProviderMap()
    {
        ResourceTags.TryBuild(RunEnv, Json("{}"), Now, out var tags, out _);
        var providerTags = new Dictionary<string, string> { ["existing"] = "keep" };

        tags.ApplyTo(providerTags);

        Assert.Equal("keep", providerTags["existing"]);
        Assert.Equal(tags.Count + 1, providerTags.Count);
        Assert.Equal(tags[ResourceTags.ExpiresAtKey], providerTags[ResourceTags.ExpiresAtKey]);
    }

    [Fact]
    public void Keys_AreValidAcrossProvidersAsGcpLabels()
    {
        // GCP label keys: lowercase letters, digits, dashes, underscores; max 63 chars.
        foreach (var key in new[] { ResourceTags.RunIdKey, ResourceTags.TenantIdKey, ResourceTags.ExpiresAtKey })
            Assert.Matches("^[a-z][a-z0-9_-]{0,62}$", key);
    }

    [Fact]
    public void WorkerSnapshotEnv_CarriesRunAndTenantIds()
    {
        var run = new DeploymentRun { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), SnapshotJson = "{}" };

        var env = RunWorkerService.BuildSnapshotEnvVars(run);

        Assert.Equal(run.Id.ToString(), env[ResourceTags.RunIdEnvVar]);
        Assert.Equal(run.TenantId.ToString(), env[ResourceTags.TenantIdEnvVar]);
    }
}
