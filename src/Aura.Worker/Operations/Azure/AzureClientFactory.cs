using Azure.Identity;
using Azure.ResourceManager;

namespace Aura.Worker.Operations.Azure;

public static class AzureClientFactory
{
    public static ArmClient Create(Dictionary<string, string> envVars)
    {
        if (envVars.TryGetValue("AZURE_TENANT_ID", out var tenantId) &&
            envVars.TryGetValue("AZURE_CLIENT_ID", out var clientId) &&
            envVars.TryGetValue("AZURE_CLIENT_SECRET", out var clientSecret))
        {
            return new ArmClient(new ClientSecretCredential(tenantId, clientId, clientSecret));
        }

        return new ArmClient(new DefaultAzureCredential());
    }
}
