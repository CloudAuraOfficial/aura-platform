using System.Text.Json;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// Create-or-update a CloudFormation stack, then wait for a terminal state.
/// Surfaces stack Outputs in the result string so downstream layers can read
/// IDs by name (parity with how the ARM template handler returns Azure outputs).
///
/// Parameters:
///   stackName        (required)
///   templateBody     (required if templateUrl absent) — inline JSON/YAML
///   templateUrl      (required if templateBody absent) — S3 URL
///   parameters       (optional) — object map: { paramKey: paramValue, ... }
///   capabilities     (optional, default ["CAPABILITY_IAM"])
///   timeoutSeconds   (optional, default 1200)
/// </summary>
public class DeployCloudFormationHandler : IOperationHandler
{
    private static readonly HashSet<string> TerminalStates = new(StringComparer.Ordinal)
    {
        "CREATE_COMPLETE", "UPDATE_COMPLETE",
        "CREATE_FAILED", "ROLLBACK_FAILED", "ROLLBACK_COMPLETE",
        "UPDATE_ROLLBACK_FAILED", "UPDATE_ROLLBACK_COMPLETE",
        "DELETE_COMPLETE", "DELETE_FAILED",
    };

    private static readonly HashSet<string> SuccessStates = new(StringComparer.Ordinal)
    {
        "CREATE_COMPLETE", "UPDATE_COMPLETE",
    };

    private readonly ILogger<DeployCloudFormationHandler> _logger;
    public DeployCloudFormationHandler(ILogger<DeployCloudFormationHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("stackName", out var snProp) || snProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: stackName");

        var templateBody = parameters.TryGetProperty("templateBody", out var tb) && tb.ValueKind == JsonValueKind.String
            ? tb.GetString() : null;
        var templateUrl = parameters.TryGetProperty("templateUrl", out var tu) && tu.ValueKind == JsonValueKind.String
            ? tu.GetString() : null;
        if (string.IsNullOrEmpty(templateBody) && string.IsNullOrEmpty(templateUrl))
            return new LayerExecutionResult(false, "One of templateBody or templateUrl is required.");

        var stackName = snProp.GetString()!;
        var timeoutSeconds = parameters.TryGetProperty("timeoutSeconds", out var toProp) ? toProp.GetInt32() : 1200;

        var cfnParams = new List<Parameter>();
        if (parameters.TryGetProperty("parameters", out var paramObj) && paramObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in paramObj.EnumerateObject())
            {
                cfnParams.Add(new Parameter
                {
                    ParameterKey = p.Name,
                    ParameterValue = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText(),
                });
            }
        }

        var capabilities = new List<string> { "CAPABILITY_IAM" };
        if (parameters.TryGetProperty("capabilities", out var capProp) && capProp.ValueKind == JsonValueKind.Array)
        {
            capabilities = capProp.EnumerateArray().Select(c => c.GetString()!).ToList();
        }

        using var cfn = AwsClientFactory.CreateCloudFormation(envVars);

        try
        {
            var exists = await StackExistsAsync(cfn, stackName, ct);

            if (exists)
            {
                _logger.LogInformation("Updating CloudFormation stack {StackName}", stackName);
                try
                {
                    await cfn.UpdateStackAsync(new UpdateStackRequest
                    {
                        StackName = stackName,
                        TemplateBody = templateBody,
                        TemplateURL = templateUrl,
                        Parameters = cfnParams,
                        Capabilities = capabilities,
                        Tags = AuraTags(layerName),
                    }, ct);
                }
                catch (AmazonCloudFormationException ex) when (ex.Message.Contains("No updates are to be performed"))
                {
                    return new LayerExecutionResult(true, $"Stack '{stackName}' has no updates to apply.");
                }
            }
            else
            {
                _logger.LogInformation("Creating CloudFormation stack {StackName}", stackName);
                await cfn.CreateStackAsync(new CreateStackRequest
                {
                    StackName = stackName,
                    TemplateBody = templateBody,
                    TemplateURL = templateUrl,
                    Parameters = cfnParams,
                    Capabilities = capabilities,
                    Tags = AuraTags(layerName),
                }, ct);
            }

            var stack = await WaitForTerminalAsync(cfn, stackName, timeoutSeconds, ct);
            if (stack is null)
                return new LayerExecutionResult(false, $"Stack '{stackName}' did not reach a terminal state within {timeoutSeconds}s.");

            var success = SuccessStates.Contains(stack.StackStatus.Value);
            var outputSummary = stack.Outputs.Count > 0
                ? string.Join(", ", stack.Outputs.Select(o => $"{o.OutputKey}={o.OutputValue}"))
                : "(no outputs)";

            return new LayerExecutionResult(success,
                $"Stack '{stackName}' status={stack.StackStatus.Value}; outputs: {outputSummary}");
        }
        catch (AmazonCloudFormationException ex)
        {
            _logger.LogError(ex, "Failed to deploy stack {StackName}", stackName);
            return new LayerExecutionResult(false, $"Failed to deploy stack: {ex.ErrorCode} — {ex.Message}");
        }
    }

    private static async Task<bool> StackExistsAsync(AmazonCloudFormationClient cfn, string stackName, CancellationToken ct)
    {
        try
        {
            var resp = await cfn.DescribeStacksAsync(new DescribeStacksRequest { StackName = stackName }, ct);
            var status = resp.Stacks.FirstOrDefault()?.StackStatus.Value;
            // REVIEW_IN_PROGRESS and DELETE_COMPLETE behave like non-existent for our purposes
            return status is not null and not "DELETE_COMPLETE";
        }
        catch (AmazonCloudFormationException ex) when (ex.Message.Contains("does not exist"))
        {
            return false;
        }
    }

    private static async Task<Stack?> WaitForTerminalAsync(AmazonCloudFormationClient cfn, string stackName, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10), ct);

            var resp = await cfn.DescribeStacksAsync(new DescribeStacksRequest { StackName = stackName }, ct);
            var stack = resp.Stacks.FirstOrDefault();
            if (stack is null) continue;
            if (TerminalStates.Contains(stack.StackStatus.Value)) return stack;
        }
        return null;
    }

    private static List<Tag> AuraTags(string layerName) => new()
    {
        new Tag { Key = "aura:layer", Value = layerName },
        new Tag { Key = "aura:managed", Value = "true" },
    };
}
