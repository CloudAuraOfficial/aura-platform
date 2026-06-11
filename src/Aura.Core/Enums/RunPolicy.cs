namespace Aura.Core.Enums;

/// <summary>
/// When a layer executes relative to earlier failures in the run (#13).
/// </summary>
public enum RunPolicy
{
    /// <summary>Default: skipped once any earlier layer has failed (fail-stop).</summary>
    OnSuccess = 0,

    /// <summary>
    /// Finally-semantics: executes even after earlier failures. Skipped only when
    /// one of its own dependsOn ancestors that is itself Always failed or was
    /// skipped. Intended for teardown/cleanup layers so a mid-run failure cannot
    /// orphan resources on unrelated clouds.
    /// </summary>
    Always = 1
}
