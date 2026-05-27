using Aura.Core.Enums;

namespace Aura.Core.Utilities;

/// <summary>
/// Cross-cloud region name mapping. Each entry pins a logical "geo" — e.g. "us-east"
/// or "eu-west" — to the per-provider region slugs. Lets multi-cloud Essences
/// (Epic 3) target a common geo without hardcoding provider-specific names.
/// </summary>
public static class RegionNormalizer
{
    // canonical -> (Azure, AWS, GCP)
    private static readonly Dictionary<string, (string Azure, string Aws, string Gcp)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["us-east"]      = ("eastus",            "us-east-1",      "us-east1"),
            ["us-east-2"]    = ("eastus2",           "us-east-2",      "us-east4"),
            ["us-west"]      = ("westus",            "us-west-1",      "us-west2"),
            ["us-west-2"]    = ("westus2",           "us-west-2",      "us-west1"),
            ["eu-west"]      = ("westeurope",        "eu-west-1",      "europe-west1"),
            ["eu-north"]     = ("northeurope",       "eu-north-1",     "europe-north1"),
            ["eu-central"]   = ("germanywestcentral","eu-central-1",   "europe-west3"),
            ["uk-south"]     = ("uksouth",           "eu-west-2",      "europe-west2"),
            ["ap-southeast"] = ("southeastasia",     "ap-southeast-1", "asia-southeast1"),
            ["ap-northeast"] = ("japaneast",         "ap-northeast-1", "asia-northeast1"),
            ["ap-south"]     = ("centralindia",      "ap-south-1",     "asia-south1"),
        };

    private static readonly Dictionary<(CloudProvider, string), string> Reverse = BuildReverse();

    /// <summary>
    /// Translate a canonical geo (e.g. "us-east") into the provider-specific
    /// region slug. Returns null if the canonical key is unknown.
    /// </summary>
    public static string? ToProvider(CloudProvider provider, string canonical)
    {
        if (!Map.TryGetValue(canonical, out var triple)) return null;
        return provider switch
        {
            CloudProvider.Azure => triple.Azure,
            CloudProvider.Aws   => triple.Aws,
            CloudProvider.Gcp   => triple.Gcp,
            _ => null
        };
    }

    /// <summary>
    /// Translate a provider-specific region slug back to its canonical geo key.
    /// Case-insensitive. Returns null if the slug isn't registered for that provider.
    /// </summary>
    public static string? ToCanonical(CloudProvider provider, string providerRegion)
    {
        return Reverse.TryGetValue((provider, providerRegion.ToLowerInvariant()), out var canonical)
            ? canonical
            : null;
    }

    /// <summary>True when both regions resolve to the same canonical geo.</summary>
    public static bool SameGeo(CloudProvider a, string regionA, CloudProvider b, string regionB)
    {
        var ca = ToCanonical(a, regionA);
        var cb = ToCanonical(b, regionB);
        return ca is not null && ca == cb;
    }

    public static IReadOnlyCollection<string> KnownCanonicalRegions => Map.Keys;

    private static Dictionary<(CloudProvider, string), string> BuildReverse()
    {
        var dict = new Dictionary<(CloudProvider, string), string>();
        foreach (var (canonical, triple) in Map)
        {
            dict[(CloudProvider.Azure, triple.Azure.ToLowerInvariant())] = canonical;
            dict[(CloudProvider.Aws,   triple.Aws.ToLowerInvariant())]   = canonical;
            dict[(CloudProvider.Gcp,   triple.Gcp.ToLowerInvariant())]   = canonical;
        }
        return dict;
    }
}
