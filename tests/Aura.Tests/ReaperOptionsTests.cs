using Aura.Worker.Reaper;
using Xunit;

namespace Aura.Tests;

/// <summary>#53: the reaper is off unless opted in, and dry-run unless deletes are explicitly allowed.</summary>
public class ReaperOptionsTests
{
    private static ReaperOptions Parse(params (string key, string value)[] env)
    {
        var map = env.ToDictionary(e => e.key, e => e.value);
        return ReaperOptions.FromEnvironment(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void NothingSet_DisabledAndDryRun()
    {
        var options = Parse();

        Assert.False(options.Enabled);
        Assert.True(options.DryRun);
        Assert.Equal(TimeSpan.FromSeconds(ReaperOptions.DefaultIntervalSeconds), options.Interval);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("yes")]
    public void ReaperEnabled_TruthyValues_Enable(string value)
    {
        Assert.True(Parse(("REAPER_ENABLED", value)).Enabled);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("on-ish")]
    [InlineData("")]
    public void ReaperEnabled_AnythingElse_StaysDisabled(string value)
    {
        Assert.False(Parse(("REAPER_ENABLED", value)).Enabled);
    }

    [Fact]
    public void EnabledWithoutDryRunSetting_IsDryRun()
    {
        var options = Parse(("REAPER_ENABLED", "true"));

        Assert.True(options.Enabled);
        Assert.True(options.DryRun);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("disabled-by-typo")]
    public void DryRun_UnrecognisedValue_StaysDryRun(string value)
    {
        Assert.True(Parse(("REAPER_ENABLED", "true"), ("REAPER_DRY_RUN", value)).DryRun);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("no")]
    public void DryRun_ExplicitFalse_AllowsDeletes(string value)
    {
        Assert.False(Parse(("REAPER_ENABLED", "true"), ("REAPER_DRY_RUN", value)).DryRun);
    }

    [Fact]
    public void Interval_BelowMinimum_ClampedToSixtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), Parse(("REAPER_INTERVAL_SECONDS", "5")).Interval);
    }

    [Fact]
    public void Interval_Unparseable_UsesDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(900), Parse(("REAPER_INTERVAL_SECONDS", "soon")).Interval);
    }
}
