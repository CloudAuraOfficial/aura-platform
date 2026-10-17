namespace Aura.Worker.Reaper;

/// <summary>
/// Opt-in settings for the expiry reaper (#53). Read once at startup.
///
///   REAPER_ENABLED=true        turns the reaper on. Unset or anything else: off, and the service exits at startup.
///   REAPER_DRY_RUN=false       allows deletes. Unset, empty or any unrecognised value: dry run (log only).
///   REAPER_INTERVAL_SECONDS    sweep interval, default 900, minimum 60.
/// </summary>
public sealed record ReaperOptions(bool Enabled, bool DryRun, TimeSpan Interval)
{
    public const int DefaultIntervalSeconds = 900;
    public const int MinIntervalSeconds = 60;

    public static ReaperOptions FromEnvironment(Func<string, string?> read)
    {
        var enabled = IsTruthy(read("REAPER_ENABLED"));

        // Deletes happen only on an explicit "false". Everything else, including a typo, stays a dry run.
        var dryRun = !IsFalsy(read("REAPER_DRY_RUN"));

        var seconds = int.TryParse(read("REAPER_INTERVAL_SECONDS"), out var parsed)
            ? Math.Max(parsed, MinIntervalSeconds)
            : DefaultIntervalSeconds;

        return new ReaperOptions(enabled, dryRun, TimeSpan.FromSeconds(seconds));
    }

    public static ReaperOptions FromEnvironment() =>
        FromEnvironment(name => Environment.GetEnvironmentVariable(name));

    private static bool IsTruthy(string? value) =>
        value?.Trim().ToLowerInvariant() is "true" or "1" or "yes";

    private static bool IsFalsy(string? value) =>
        value?.Trim().ToLowerInvariant() is "false" or "0" or "no";
}
