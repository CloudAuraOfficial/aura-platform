namespace Aura.Api.Services;

/// <summary>
/// Checks essence JSON against what the platform can run. Generation uses the full check;
/// the essence endpoints use the operation-type check when they store JSON.
/// </summary>
public interface IEssenceValidator
{
    /// <summary>
    /// Full check: JSON shape, the deployment parser, and operation types. Returns null when the
    /// essence is runnable, otherwise a short reason that is safe to show the user or feed back to a model.
    /// </summary>
    string? Validate(string essenceJson);

    /// <summary>
    /// Operation-type check only. Returns null when no layer names an unknown operationType.
    /// Layers are checked whether enabled or not, so a typo cannot hide in a disabled layer.
    /// Input that is not a JSON object with a layers object passes; structure is not checked here.
    /// </summary>
    string? ValidateOperationTypes(string essenceJson);
}
