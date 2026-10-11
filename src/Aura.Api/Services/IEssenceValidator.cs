using Aura.Core.Enums;

namespace Aura.Api.Services;

/// <summary>
/// The outcome of checking essence JSON: the JSON to store (operation-type names in canonical casing)
/// and, when the essence is rejected, a short reason that is safe to show the user or feed back to a model.
/// </summary>
public sealed record EssenceCheck(string EssenceJson, string? Error);

/// <summary>
/// Checks essence JSON against what the platform can run. Only operation and emissionload layers are
/// checked; script layers may carry parameters.operationType as a plain argument.
/// </summary>
public interface IEssenceValidator
{
    /// <summary>
    /// Generation: JSON shape, the deployment parser, and the operation types valid for <paramref name="cloud"/>.
    /// A rejection names the layer and type and lists only that cloud's types, so a retry stays on that cloud.
    /// </summary>
    EssenceCheck Validate(string essenceJson, CloudProvider cloud);

    /// <summary>
    /// Save, update and clone: every layer's operation type must be a known one, on any cloud. Essences may
    /// mix clouds, because the Worker gives each layer its own cloud account and does not check that an
    /// operation matches it. Input that is not a JSON object with a layers object passes; structure is not checked here.
    /// </summary>
    EssenceCheck CheckOperationTypes(string essenceJson);

    /// <summary>
    /// Deployment validation: the same parser run creation uses, then the operation-type checks save applies
    /// on any cloud. A rejection is what run creation or the save path would fail on, so a deployment that
    /// passes here is one whose essence is accepted by both.
    /// </summary>
    EssenceCheck CheckRunnable(string essenceJson);
}
