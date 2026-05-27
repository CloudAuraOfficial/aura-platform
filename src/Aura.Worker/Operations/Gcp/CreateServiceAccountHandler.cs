using System.Text.Json;
using Aura.Worker.Executors;
using Google.Cloud.Iam.Admin.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Gcp;

/// <summary>
/// Creates a GCP service account inside the project. The accountId becomes
/// the email prefix (full email: accountId@<projectId>.iam.gserviceaccount.com).
/// Idempotent: if a service account with that accountId already exists, the
/// handler returns success with its email and unique ID.
///
/// Parameters:
///   accountId    (required)  — [a-z]([-a-z0-9]*[a-z0-9])?, 6-30 chars
///   displayName  (optional)
///   description  (optional)
/// </summary>
public class CreateServiceAccountHandler : IOperationHandler
{
    private readonly ILogger<CreateServiceAccountHandler> _logger;
    public CreateServiceAccountHandler(ILogger<CreateServiceAccountHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars, CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("accountId", out var aProp) || aProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: accountId");

        var accountId = aProp.GetString()!;
        var displayName = parameters.TryGetProperty("displayName", out var d) ? d.GetString()! : accountId;
        var description = parameters.TryGetProperty("description", out var ds) ? ds.GetString() : $"aura:layer={layerName}";

        var projectId = GcpClientFactory.ResolveProjectId(envVars);
        var email = $"{accountId}@{projectId}.iam.gserviceaccount.com";

        try
        {
            var client = GcpClientFactory.CreateIam(envVars);

            try
            {
                var existing = await client.GetServiceAccountAsync(new GetServiceAccountRequest
                {
                    Name = $"projects/{projectId}/serviceAccounts/{email}",
                }, ct);
                _logger.LogInformation("Service account {Email} already exists", email);
                return new LayerExecutionResult(true,
                    $"Service account '{email}' already exists (uniqueId={existing.UniqueId}).");
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // fall through to create
            }

            _logger.LogInformation("Creating service account {AccountId} in project {ProjectId}", accountId, projectId);
            var created = await client.CreateServiceAccountAsync(new CreateServiceAccountRequest
            {
                Name = $"projects/{projectId}",
                AccountId = accountId,
                ServiceAccount = new ServiceAccount
                {
                    DisplayName = displayName,
                    Description = description ?? "",
                },
            }, ct);

            return new LayerExecutionResult(true,
                $"Service account '{created.Email}' created (uniqueId={created.UniqueId}).");
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "Failed to create service account {AccountId}", accountId);
            return new LayerExecutionResult(false, $"Failed to create service account: {ex.StatusCode} — {ex.Status.Detail}");
        }
    }
}
