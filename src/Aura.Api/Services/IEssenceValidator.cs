using Aura.Core.Enums;

namespace Aura.Api.Services;

/// <summary>
/// The outcome of checking essence JSON: the JSON to store (operation-type names in canonical casing) and every
/// reason the essence is rejected. The reasons are safe to show the user or feed back to a model.
/// </summary>
public sealed record EssenceCheck(string EssenceJson, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>All reasons on one line, for callers that show a single message. Null when valid.</summary>
    public string? Error => Errors.Count == 0 ? null : string.Join(" ", Errors);
}

/// <summary>
/// Checks essence JSON against what the platform can run. Only operation and emissionload layers are
/// checked; script layers may carry parameters.operationType as a plain argument.
/// </summary>
public interface IEssenceValidator
{
    /// <summary>
    /// The deployment parser run creation uses, then the operation types. With <paramref name="cloud"/> set,
    /// types must be valid for that cloud (generation), so a retry stays on that cloud. With null, types must be
    /// known on any cloud (deployment validation). Layers that run creation skips (disabled) are not checked.
    /// Pass <paramref name="canonicalize"/> false when the stored JSON is not needed, which skips the rewrite.
    /// Every problem is returned, not just the first.
    /// </summary>
    EssenceCheck Validate(string essenceJson, CloudProvider? cloud, bool canonicalize = true);

    /// <summary>
    /// Save, update and clone: every layer's operation type must be a known one, on any cloud, disabled layers
    /// included. Essences may mix clouds, because the Worker gives each layer its own cloud account and does not
    /// check that an operation matches it. Input that is not a JSON object with a layers object passes; structure
    /// is not checked here.
    /// </summary>
    EssenceCheck CheckOperationTypes(string essenceJson);
}
