using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Iam.Admin.V1;
using Google.Cloud.Storage.V1;
using ComputeV1 = Google.Cloud.Compute.V1;
using RunV2 = Google.Cloud.Run.V2;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// Builds GCP service clients from BYOS credentials in envVars.
///
/// Accepts either of two shapes:
///
///   Canonical (preferred):
///     GCP_SERVICE_ACCOUNT_JSON  — full SA key file as a single JSON string value
///     GCP_PROJECT_ID            — target project
///
///   Dashboard-friendly (the SA file pasted as a JSON object — its keys become envVars):
///     type, project_id, private_key, private_key_id, client_email, client_id,
///     token_uri, auth_uri, … (i.e. the SA fields are flattened into envVars)
///     plus optional GCP_PROJECT_ID / project_id / projectId
///
/// When the flattened shape is detected the factory rebuilds the canonical SA JSON
/// in memory before passing it to GoogleCredential.FromJson. Nothing is logged.
///
/// If neither shape is present the SDK's ADC chain runs (useful for local dev with
/// `gcloud auth application-default login`).
/// </summary>
public static class GcpClientFactory
{
    public const string ServiceAccountJsonKey = "GCP_SERVICE_ACCOUNT_JSON";
    public const string ProjectIdKey = "GCP_PROJECT_ID";

    private static readonly string[] FlattenedSaKeys =
    {
        "type", "project_id", "private_key_id", "private_key",
        "client_email", "client_id", "auth_uri", "token_uri",
        "auth_provider_x509_cert_url", "client_x509_cert_url", "universe_domain"
    };

    public static string ResolveProjectId(Dictionary<string, string> envVars) =>
        envVars.GetValueOrDefault(ProjectIdKey)
            ?? envVars.GetValueOrDefault("project_id")
            ?? envVars.GetValueOrDefault("projectId")
            ?? envVars.GetValueOrDefault("AURA_SUBSCRIPTION_ID")
            ?? throw new InvalidOperationException(
                $"GCP project id missing. Set {ProjectIdKey} (or project_id / projectId), " +
                "or baseEssence.subscriptionId.");

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

        var rebuilt = TryRebuildFromFlattened(envVars);
        if (rebuilt is not null)
        {
            return GoogleCredential.FromJson(rebuilt);
        }

        return GoogleCredential.GetApplicationDefault();
    }

    private static string? TryRebuildFromFlattened(Dictionary<string, string> envVars)
    {
        // Need at least these to make a usable SA JSON.
        if (!envVars.ContainsKey("private_key") || !envVars.ContainsKey("client_email"))
            return null;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var key in FlattenedSaKeys)
            {
                if (envVars.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                    writer.WriteString(key, v);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
