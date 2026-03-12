using Aura.Core.Services;
using Xunit;

namespace Aura.Tests;

public class CronExpressionTests
{
    // --- Parsing ---

    [Fact]
    public void Parse_EveryMinute_MatchesAll()
    {
        var cron = CronExpression.Parse("* * * * *");
        var dt = new DateTime(2026, 3, 12, 14, 30, 0, DateTimeKind.Utc);
        Assert.True(cron.Matches(dt));
    }

    [Fact]
    public void Parse_SpecificMinuteAndHour()
    {
        var cron = CronExpression.Parse("30 14 * * *");
        Assert.True(cron.Matches(new DateTime(2026, 3, 12, 14, 30, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 3, 12, 14, 31, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 3, 12, 15, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Parse_Step_EveryFifteenMinutes()
    {
        var cron = CronExpression.Parse("*/15 * * * *");
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 0, 15, 0, DateTimeKind.Utc)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 0, 45, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Parse_Range()
    {
        var cron = CronExpression.Parse("0 9-17 * * *");
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 17, 0, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 18, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Parse_List()
    {
        var cron = CronExpression.Parse("0,30 * * * *");
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 12, 15, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Parse_RangeWithStep()
    {
        var cron = CronExpression.Parse("0-30/10 * * * *");
        // Matches: 0, 10, 20, 30
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 0, 20, 0, DateTimeKind.Utc)));
        Assert.True(cron.Matches(new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 1, 0, 40, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Parse_DayOfWeek_Weekdays()
    {
        // Monday=1 through Friday=5
        var cron = CronExpression.Parse("0 9 * * 1-5");
        // 2026-03-12 is a Thursday (DayOfWeek=4)
        Assert.True(cron.Matches(new DateTime(2026, 3, 12, 9, 0, 0, DateTimeKind.Utc)));
        // 2026-03-15 is a Sunday (DayOfWeek=0)
        Assert.False(cron.Matches(new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Parse_SpecificMonthAndDay()
    {
        var cron = CronExpression.Parse("0 0 1 6 *");
        Assert.True(cron.Matches(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(cron.Matches(new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Parse_ComplexExpression()
    {
        // Every 5 min, hours 8-20, weekdays, Jan-Jun
        var cron = CronExpression.Parse("*/5 8-20 * 1-6 1-5");
        // Thursday 2026-03-12 10:15 UTC
        Assert.True(cron.Matches(new DateTime(2026, 3, 12, 10, 15, 0, DateTimeKind.Utc)));
        // Sunday
        Assert.False(cron.Matches(new DateTime(2026, 3, 15, 10, 15, 0, DateTimeKind.Utc)));
        // July (month 7)
        Assert.False(cron.Matches(new DateTime(2026, 7, 13, 10, 15, 0, DateTimeKind.Utc)));
        // Minute 11 (not divisible by 5)
        Assert.False(cron.Matches(new DateTime(2026, 3, 12, 10, 11, 0, DateTimeKind.Utc)));
    }

    // --- Matches ignores seconds ---

    [Fact]
    public void Matches_IgnoresSeconds()
    {
        var cron = CronExpression.Parse("30 14 * * *");
        Assert.True(cron.Matches(new DateTime(2026, 3, 12, 14, 30, 45, DateTimeKind.Utc)));
    }

    // --- Error cases ---

    [Fact]
    public void Parse_TooFewFields_Throws()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse("* * *"));
    }

    [Fact]
    public void Parse_TooManyFields_Throws()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse("* * * * * *"));
    }

    [Fact]
    public void Parse_OutOfRange_Throws()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse("60 * * * *"));
        Assert.Throws<FormatException>(() => CronExpression.Parse("* 25 * * *"));
        Assert.Throws<FormatException>(() => CronExpression.Parse("* * 0 * *"));
        Assert.Throws<FormatException>(() => CronExpression.Parse("* * * 13 *"));
        Assert.Throws<FormatException>(() => CronExpression.Parse("* * * * 7"));
    }

    [Fact]
    public void Parse_InvalidStep_Throws()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse("*/0 * * * *"));
        Assert.Throws<FormatException>(() => CronExpression.Parse("*/abc * * * *"));
    }

    [Fact]
    public void Parse_InvalidRange_Throws()
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse("5-2 * * * *"));
    }

    [Fact]
    public void Parse_EmptyOrWhitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => CronExpression.Parse(""));
        Assert.Throws<ArgumentException>(() => CronExpression.Parse("   "));
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CronExpression.Parse(null!));
    }

    // --- TryParse ---

    [Fact]
    public void TryParse_Valid_ReturnsCron()
    {
        var cron = CronExpression.TryParse("*/5 * * * *");
        Assert.NotNull(cron);
    }

    [Fact]
    public void TryParse_Invalid_ReturnsNull()
    {
        Assert.Null(CronExpression.TryParse(null));
        Assert.Null(CronExpression.TryParse(""));
        Assert.Null(CronExpression.TryParse("not a cron"));
    }

    // --- ParseField directly ---

    [Fact]
    public void ParseField_Wildcard()
    {
        var result = CronExpression.ParseField("*", 0, 59);
        Assert.Equal(60, result.Count);
        Assert.Contains(0, result);
        Assert.Contains(59, result);
    }

    [Fact]
    public void ParseField_WildcardWithStep()
    {
        var result = CronExpression.ParseField("*/10", 0, 59);
        Assert.Equal(new HashSet<int> { 0, 10, 20, 30, 40, 50 }, result);
    }

    [Fact]
    public void ParseField_SingleValue()
    {
        var result = CronExpression.ParseField("5", 0, 59);
        Assert.Single(result);
        Assert.Contains(5, result);
    }

    [Fact]
    public void ParseField_List()
    {
        var result = CronExpression.ParseField("1,15,30", 0, 59);
        Assert.Equal(new HashSet<int> { 1, 15, 30 }, result);
    }

    [Fact]
    public void ParseField_Range()
    {
        var result = CronExpression.ParseField("3-7", 1, 31);
        Assert.Equal(new HashSet<int> { 3, 4, 5, 6, 7 }, result);
    }
}
