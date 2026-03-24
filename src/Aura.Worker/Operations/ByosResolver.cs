using System.Text.RegularExpressions;

namespace Aura.Worker.Operations;

public static class ByosResolver
{
    private static readonly Regex ByosPattern = new(@"\$\{BYOS_([^}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Scans a JSON parameters string for ${BYOS_*} references and replaces them
    /// with values from the decrypted credentials dictionary.
    /// </summary>
    public static string Resolve(string parametersJson, Dictionary<string, string> credentials)
    {
        return ByosPattern.Replace(parametersJson, match =>
        {
            var key = match.Groups[1].Value;
            if (!credentials.TryGetValue(key, out var value))
                throw new InvalidOperationException(
                    $"BYOS credential key '{key}' not found. " +
                    $"Available keys: [{string.Join(", ", credentials.Keys)}]");
            return value;
        });
    }

    /// <summary>
    /// Populates environment variables from decrypted credentials.
    /// Each credential key becomes an env var directly.
    /// </summary>
    public static void PopulateEnvVars(Dictionary<string, string> envVars, Dictionary<string, string> credentials)
    {
        foreach (var (key, value) in credentials)
        {
            envVars[key] = value;
        }
    }
}
