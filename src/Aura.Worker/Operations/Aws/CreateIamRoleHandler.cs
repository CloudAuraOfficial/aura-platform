using System.Text.Json;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// Creates an IAM role with the given trust policy and (optionally) attaches
/// managed policies. Idempotent: if a role with the same name already exists
/// the handler succeeds without changes — same shape as Azure's
/// CreateOrUpdate operations.
///
/// Parameters:
///   roleName            (required)
///   assumeRolePolicy    (required)  — JSON trust policy as a string
///   description         (optional)
///   managedPolicyArns   (optional)  — array of AWS managed policy ARNs to attach
///   path                (optional, default "/")
/// </summary>
public class CreateIamRoleHandler : IOperationHandler
{
    private readonly ILogger<CreateIamRoleHandler> _logger;
    public CreateIamRoleHandler(ILogger<CreateIamRoleHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("roleName", out var nProp) || nProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: roleName");
        if (!parameters.TryGetProperty("assumeRolePolicy", out var arpProp))
            return new LayerExecutionResult(false, "Missing required parameter: assumeRolePolicy");

        var roleName = nProp.GetString()!;
        // Trust policy may be a JSON string OR an inline object — handle both.
        var assumeRolePolicy = arpProp.ValueKind == JsonValueKind.String
            ? arpProp.GetString()!
            : arpProp.GetRawText();
        var description = parameters.TryGetProperty("description", out var dProp) ? dProp.GetString() : null;
        var path = parameters.TryGetProperty("path", out var pProp) ? pProp.GetString() : "/";
        var managedPolicyArns = parameters.TryGetProperty("managedPolicyArns", out var mpProp) && mpProp.ValueKind == JsonValueKind.Array
            ? mpProp.EnumerateArray().Select(p => p.GetString()!).ToList()
            : new List<string>();

        using var iam = AwsClientFactory.CreateIam(envVars);

        try
        {
            string roleArn;
            try
            {
                _logger.LogInformation("Creating IAM role {RoleName}", roleName);
                var resp = await iam.CreateRoleAsync(new CreateRoleRequest
                {
                    RoleName = roleName,
                    AssumeRolePolicyDocument = assumeRolePolicy,
                    Description = description,
                    Path = path,
                    Tags = new List<Tag>
                    {
                        new() { Key = "aura:layer", Value = layerName },
                        new() { Key = "aura:managed", Value = "true" },
                    },
                }, ct);
                roleArn = resp.Role.Arn;
            }
            catch (EntityAlreadyExistsException)
            {
                _logger.LogInformation("IAM role {RoleName} already exists — skipping create", roleName);
                var existing = await iam.GetRoleAsync(new GetRoleRequest { RoleName = roleName }, ct);
                roleArn = existing.Role.Arn;
            }

            foreach (var policyArn in managedPolicyArns)
            {
                _logger.LogInformation("Attaching policy {PolicyArn} to role {RoleName}", policyArn, roleName);
                try
                {
                    await iam.AttachRolePolicyAsync(new AttachRolePolicyRequest
                    {
                        RoleName = roleName,
                        PolicyArn = policyArn,
                    }, ct);
                }
                catch (AmazonIdentityManagementServiceException ex) when (ex.ErrorCode == "EntityAlreadyExists" || ex.Message.Contains("already attached"))
                {
                    // already attached — fine
                }
            }

            return new LayerExecutionResult(true,
                $"IAM role '{roleName}' ready (arn={roleArn}, attached={managedPolicyArns.Count} policy/policies).");
        }
        catch (AmazonIdentityManagementServiceException ex)
        {
            _logger.LogError(ex, "Failed to create IAM role {RoleName}", roleName);
            return new LayerExecutionResult(false, $"Failed to create IAM role: {ex.ErrorCode} — {ex.Message}");
        }
    }
}
