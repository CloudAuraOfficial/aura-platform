using Google.Apis.Auth.OAuth2;
using Google.Cloud.Iam.Admin.V1;
using Google.Cloud.Storage.V1;
using ComputeV1 = Google.Cloud.Compute.V1;
using RunV2 = Google.Cloud.Run.V2;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// Builds GCP service clients from BYOS credentials passed in via envVars.
///
/// Required keys:  GCP_SERVICE_ACCOUNT_JSON  — full SA key file as a JSON string
///                 GCP_PROJECT_ID            — target project (may also live in the SA JSON)
///
/// The SA JSON is passed directly to GoogleCredential.FromJson — no temp
/// files, no per-field reconstruction, never logged. When the env var is
/// missing the SDK's ADC chain runs (useful for local dev with `gcloud auth
/// application-default login`).
///
/// Project ID is read once per factory call and exposed via ProjectId for
/// handlers that need it in API calls.
/// </summary>
public static class GcpClientFactory
{
    public const string ServiceAccountJsonKey = "GCP_SERVICE_ACCOUNT_JSON";
    public const string ProjectIdKey = "GCP_PROJECT_ID";

    public static string ResolveProjectId(Dictionary<string, string> envVars) =>
        envVars.GetValueOrDefault(ProjectIdKey)
            ?? envVars.GetValueOrDefault("AURA_SUBSCRIPTION_ID")
            ?? throw new InvalidOperationException(
                $"GCP project id missing. Set {ProjectIdKey} or baseEssence.subscriptionId.");

    public static ComputeV1.InstancesClient CreateInstances(Dictionary<string, string> envVars) =>
        new ComputeV1.InstancesClientBuilder { Credential = GetCredential(envVars) }.Build();

    public static ComputeV1.NetworksClient CreateNetworks(Dictionary<string, string> envVars) =>
        new ComputeV1.NetworksClientBuilder { Credential = GetCredential(envVars) }.Build();

    public static ComputeV1.SubnetworksClient CreateSubnetworks(Dictionary<string, string> envVars) =>
        new ComputeV1.SubnetworksClientBuilder { Credential = GetCredential(envVars) }.Build();

    public static ComputeV1.FirewallsClient CreateFirewalls(Dictionary<string, string> envVars) =>
        new ComputeV1.FirewallsClientBuilder { Credential = GetCredential(envVars) }.Build();

    public static StorageClient CreateStorage(Dictionary<string, string> envVars) =>
        StorageClient.Create(GetCredential(envVars));

    public static RunV2.ServicesClient CreateCloudRunServices(Dictionary<string, string> envVars) =>
        new RunV2.ServicesClientBuilder { Credential = GetCredential(envVars) }.Build();

    public static IAMClient CreateIam(Dictionary<string, string> envVars) =>
        new IAMClientBuilder { Credential = GetCredential(envVars) }.Build();

    private static GoogleCredential GetCredential(Dictionary<string, string> envVars)
    {
        if (envVars.TryGetValue(ServiceAccountJsonKey, out var saJson) && !string.IsNullOrWhiteSpace(saJson))
        {
            return GoogleCredential.FromJson(saJson);
        }
        return GoogleCredential.GetApplicationDefault();
    }
}
