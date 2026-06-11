using System.Text.Json;
using Amazon.EC2;
using Amazon.EC2.Model;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// EC2 lifecycle handlers (Start, Stop, Terminate). All three look up the
/// target instance by its Name tag (instanceName param) so Essences don't
/// have to thread instance IDs between layers — matches the Azure VM
/// lifecycle handlers, which look up by name within a resource group.
///
/// Terminate also deletes the tagged security group once the instance has
/// shut down enough for the SG to no longer be in use (best-effort: a
/// trailing "still in use" error is swallowed).
/// </summary>
internal static class Ec2InstanceLookup
{
    public static async Task<Instance?> FindByNameAsync(
        AmazonEC2Client ec2, string instanceName, CancellationToken ct)
    {
        var resp = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest
        {
            Filters = new List<Filter>
            {
                new() { Name = "tag:Name", Values = new List<string> { instanceName } },
                new() { Name = "instance-state-name", Values = new List<string>
                    { "pending", "running", "stopping", "stopped" } },
            },
        }, ct);
        return resp.Reservations.SelectMany(r => r.Instances).FirstOrDefault();
    }
}

public class StartEc2InstanceHandler : IOperationHandler
{
    private readonly ILogger<StartEc2InstanceHandler> _logger;
    public StartEc2InstanceHandler(ILogger<StartEc2InstanceHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("instanceName", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: instanceName");
        var instanceName = nameProp.GetString()!;

        using var ec2 = AwsClientFactory.CreateEc2(envVars);
        try
        {
            var instance = await Ec2InstanceLookup.FindByNameAsync(ec2, instanceName, ct);
            if (instance is null)
                return new LayerExecutionResult(false, $"EC2 instance '{instanceName}' not found.");

            _logger.LogInformation("Starting EC2 {InstanceId} ({InstanceName})", instance.InstanceId, instanceName);
            await ec2.StartInstancesAsync(new StartInstancesRequest
            {
                InstanceIds = new List<string> { instance.InstanceId },
            }, ct);
            return new LayerExecutionResult(true, $"EC2 '{instanceName}' ({instance.InstanceId}) start requested.");
        }
        catch (AmazonEC2Exception ex)
        {
            _logger.LogError(ex, "Failed to start EC2 {InstanceName}", instanceName);
            return new LayerExecutionResult(false, $"Failed to start EC2: {ex.ErrorCode} — {ex.Message}");
        }
    }
}

public class StopEc2InstanceHandler : IOperationHandler
{
    private readonly ILogger<StopEc2InstanceHandler> _logger;
    public StopEc2InstanceHandler(ILogger<StopEc2InstanceHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("instanceName", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: instanceName");
        var instanceName = nameProp.GetString()!;

        using var ec2 = AwsClientFactory.CreateEc2(envVars);
        try
        {
            var instance = await Ec2InstanceLookup.FindByNameAsync(ec2, instanceName, ct);
            if (instance is null)
                return new LayerExecutionResult(false, $"EC2 instance '{instanceName}' not found.");

            _logger.LogInformation("Stopping EC2 {InstanceId} ({InstanceName})", instance.InstanceId, instanceName);
            await ec2.StopInstancesAsync(new StopInstancesRequest
            {
                InstanceIds = new List<string> { instance.InstanceId },
            }, ct);
            return new LayerExecutionResult(true, $"EC2 '{instanceName}' ({instance.InstanceId}) stop requested.");
        }
        catch (AmazonEC2Exception ex)
        {
            _logger.LogError(ex, "Failed to stop EC2 {InstanceName}", instanceName);
            return new LayerExecutionResult(false, $"Failed to stop EC2: {ex.ErrorCode} — {ex.Message}");
        }
    }
}

public class TerminateEc2InstanceHandler : IOperationHandler
{
    private readonly ILogger<TerminateEc2InstanceHandler> _logger;
    public TerminateEc2InstanceHandler(ILogger<TerminateEc2InstanceHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("instanceName", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: instanceName");
        var instanceName = nameProp.GetString()!;

        using var ec2 = AwsClientFactory.CreateEc2(envVars);
        try
        {
            var instance = await Ec2InstanceLookup.FindByNameAsync(ec2, instanceName, ct);
            if (instance is null)
                return new LayerExecutionResult(true, $"EC2 instance '{instanceName}' not found — nothing to terminate.");

            _logger.LogInformation("Terminating EC2 {InstanceId} ({InstanceName})", instance.InstanceId, instanceName);
            await ec2.TerminateInstancesAsync(new TerminateInstancesRequest
            {
                InstanceIds = new List<string> { instance.InstanceId },
            }, ct);

            // Wait for the instance to actually reach 'terminated'. Termination
            // is asynchronous; reporting success at 'requested' lets downstream
            // teardown layers (e.g. DeleteVpc) race the dying instance and fail
            // with DependencyViolation — the subnet still holds its ENI (#12).
            var waitDeadline = DateTime.UtcNow.AddMinutes(5);
            var terminated = false;
            while (DateTime.UtcNow < waitDeadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                var state = (await ec2.DescribeInstancesAsync(new DescribeInstancesRequest
                {
                    InstanceIds = new List<string> { instance.InstanceId },
                }, ct)).Reservations?.FirstOrDefault()?.Instances?.FirstOrDefault()?.State?.Name;
                _logger.LogInformation("  EC2 {InstanceId} state: {State}", instance.InstanceId, state?.Value);
                if (state == InstanceStateName.Terminated) { terminated = true; break; }
            }
            if (!terminated)
                return new LayerExecutionResult(false,
                    $"EC2 '{instanceName}' ({instance.InstanceId}) did not reach 'terminated' within 5 minutes.");

            // SG cleanup — safe now that the ENI is released.
            var sgName = $"{instanceName}-sg";
            try
            {
                var sgs = (await ec2.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest
                {
                    Filters = new List<Filter> { new() { Name = "group-name", Values = new List<string> { sgName } } },
                }, ct)).SecurityGroups ?? new List<SecurityGroup>();
                foreach (var sg in sgs)
                {
                    await ec2.DeleteSecurityGroupAsync(new DeleteSecurityGroupRequest { GroupId = sg.GroupId }, ct);
                }
            }
            catch (AmazonEC2Exception ex) when (ex.ErrorCode == "DependencyViolation" || ex.ErrorCode == "InvalidGroup.NotFound")
            {
                _logger.LogDebug("SG cleanup skipped for {SgName}: {ErrorCode}", sgName, ex.ErrorCode);
            }

            return new LayerExecutionResult(true, $"EC2 '{instanceName}' ({instance.InstanceId}) terminated.");
        }
        catch (AmazonEC2Exception ex)
        {
            _logger.LogError(ex, "Failed to terminate EC2 {InstanceName}", instanceName);
            return new LayerExecutionResult(false, $"Failed to terminate EC2: {ex.ErrorCode} — {ex.Message}");
        }
    }
}
