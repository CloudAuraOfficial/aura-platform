using Azure.Identity;
using Azure.ResourceManager;

namespace Aura.Worker.Operations.Azure;

/// <summary>
/// Builds the Azure ARM client from BYOS credentials in envVars.
///
/// Accepts either of two key shapes so the dashboard "Test Connection" payload
/// and the worker runtime path agree:
///
///   Canonical (preferred):
///     AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET
///
///   Dashboard-friendly (camelCase, as the Test endpoint validates):
///     tenantId, clientId, clientSecret
///
/// If none of the keys are present the SDK's DefaultAzureCredential chain runs.
/// </summary>
public static class AzureClientFactory
{
    public static ArmClient Create(Dictionary<string, string> envVars)
    {
        var tenantId = Pick(envVars, "AZURE_TENANT_ID", "tenantId");
        var clientId = Pick(envVars, "AZURE_CLIENT_ID", "clientId");
        var clientSecret = Pick(envVars, "AZURE_CLIENT_SECRET", "clientSecret");

        if (!string.IsNullOrEmpty(tenantId)
            && !string.IsNullOrEmpty(clientId)
            && !string.IsNullOrEmpty(clientSecret))
        {
            return new ArmClient(new ClientSecretCredential(tenantId, clientId, clientSecret));
        }

        return new ArmClient(new DefaultAzureCredential());
    }

    private static string? Pick(Dictionary<string, string> envVars, params string[] keys)
    {
        foreach (var k in keys)
            if (envVars.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }
}
