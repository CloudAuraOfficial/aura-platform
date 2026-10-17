using System.Globalization;
using System.Text.Json;

namespace Aura.Worker.Operations.Common;

/// <summary>
/// Expiry tags stamped on every cloud resource a layer creates (#53), so the
/// reaper can find and delete resources whose run died before teardown.
///
/// Keys (lowercase, hyphenated: valid for Azure tags, AWS tags and GCP labels):
///   aura-run-id      run GUID, from AURA_RUN_ID (omitted when the worker did not set it)
///   aura-tenant-id   tenant GUID, from AURA_TENANT_ID (omitted when absent)
///   aura-expires-at  UTC expiry, formatted as yyyyMMddHHmmss (digits only, valid for every provider)
///
/// Expiry defaults to created + 2h. A layer may override it with the
/// `expiresInMinutes` parameter (1 .. 10080, i.e. up to 7 days).
/// </summary>
public static class ResourceTags
{
    public const string RunIdKey = "aura-run-id";
    public const string TenantIdKey = "aura-tenant-id";
    public const string ExpiresAtKey = "aura-expires-at";

    public const string RunIdEnvVar = "AURA_RUN_ID";
    public const string TenantIdEnvVar = "AURA_TENANT_ID";
    public const string ExpiresInMinutesParameter = "expiresInMinutes";

    public const string ExpiresAtFormat = "yyyyMMddHHmmss";

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(2);
    private const int MaxExpiresInMinutes = 7 * 24 * 60;

    /// <summary>
    /// Builds the tag set for one layer. Returns false with a user-facing error when
    /// `expiresInMinutes` is present but not an integer in range.
    /// </summary>
    public static bool TryBuild(
        IReadOnlyDictionary<string, string> envVars,
        JsonElement parameters,
        DateTime utcNow,
        out IReadOnlyDictionary<string, string> tags,
        out string? error)
    {
        var ttl = DefaultTtl;
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty(ExpiresInMinutesParameter, out var minutesProp))
        {
            if (minutesProp.ValueKind != JsonValueKind.Number
                || !minutesProp.TryGetInt32(out var minutes)
                || minutes < 1
                || minutes > MaxExpiresInMinutes)
            {
                tags = new Dictionary<string, string>();
                error = $"{ExpiresInMinutesParameter} must be an integer between 1 and {MaxExpiresInMinutes}.";
                return false;
            }
            ttl = TimeSpan.FromMinutes(minutes);
        }

        var result = new Dictionary<string, string>
        {
            [ExpiresAtKey] = FormatExpiry(utcNow.Add(ttl)),
        };
        if (envVars.TryGetValue(RunIdEnvVar, out var runId) && !string.IsNullOrWhiteSpace(runId))
            result[RunIdKey] = runId;
        if (envVars.TryGetValue(TenantIdEnvVar, out var tenantId) && !string.IsNullOrWhiteSpace(tenantId))
            result[TenantIdKey] = tenantId;

        tags = result;
        error = null;
        return true;
    }

    /// <summary>Copies the tag set onto a provider resource's tag or label map.</summary>
    public static void ApplyTo(this IReadOnlyDictionary<string, string> tags, IDictionary<string, string> target)
    {
        foreach (var (key, value) in tags)
            target[key] = value;
    }

    public static string FormatExpiry(DateTime expiresAtUtc) =>
        expiresAtUtc.ToUniversalTime().ToString(ExpiresAtFormat, CultureInfo.InvariantCulture);

    /// <summary>Parses an aura-expires-at value. Returns false for anything that is not a valid expiry.</summary>
    public static bool TryParseExpiry(string? value, out DateTime expiresAtUtc)
    {
        expiresAtUtc = default;
        return !string.IsNullOrEmpty(value)
            && DateTime.TryParseExact(
                value, ExpiresAtFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out expiresAtUtc);
    }
}
