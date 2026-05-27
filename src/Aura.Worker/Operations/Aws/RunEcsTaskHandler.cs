using System.Text.Json;
using Amazon.ECS;
using Amazon.ECS.Model;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Aws;

/// <summary>
/// Runs a Fargate task on an existing ECS cluster + task definition.
/// Waits up to timeoutSeconds for the task to reach STOPPED, then returns
/// the container exit code. Networking is assumed to be public-subnet
/// Fargate (assignPublicIp=ENABLED).
///
/// Parameters:
///   cluster           (required)  — ECS cluster name or ARN
///   taskDefinition    (required)  — family[:revision] or full ARN
///   subnets           (required)  — array of subnet IDs
///   securityGroups    (optional)  — array of SG IDs (default: VPC's default SG)
///   timeoutSeconds    (optional, default 300)
///   assignPublicIp    (optional, default true)
/// </summary>
public class RunEcsTaskHandler : IOperationHandler
{
    private readonly ILogger<RunEcsTaskHandler> _logger;
    public RunEcsTaskHandler(ILogger<RunEcsTaskHandler> logger) => _logger = logger;

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("cluster", out var cProp) || cProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: cluster");
        if (!parameters.TryGetProperty("taskDefinition", out var tdProp) || tdProp.ValueKind != JsonValueKind.String)
            return new LayerExecutionResult(false, "Missing required parameter: taskDefinition");
        if (!parameters.TryGetProperty("subnets", out var snProp) || snProp.ValueKind != JsonValueKind.Array)
            return new LayerExecutionResult(false, "Missing required parameter: subnets (array)");

        var cluster = cProp.GetString()!;
        var taskDef = tdProp.GetString()!;
        var subnets = snProp.EnumerateArray().Select(s => s.GetString()!).ToList();
        var securityGroups = parameters.TryGetProperty("securityGroups", out var sgProp) && sgProp.ValueKind == JsonValueKind.Array
            ? sgProp.EnumerateArray().Select(s => s.GetString()!).ToList()
            : new List<string>();
        var timeoutSeconds = parameters.TryGetProperty("timeoutSeconds", out var toProp) ? toProp.GetInt32() : 300;
        var assignPublicIp = !parameters.TryGetProperty("assignPublicIp", out var apiProp) || apiProp.GetBoolean();

        using var ecs = AwsClientFactory.CreateEcs(envVars);

        try
        {
            _logger.LogInformation("Running Fargate task {TaskDef} on {Cluster}", taskDef, cluster);
            var runResp = await ecs.RunTaskAsync(new RunTaskRequest
            {
                Cluster = cluster,
                TaskDefinition = taskDef,
                LaunchType = LaunchType.FARGATE,
                Count = 1,
                NetworkConfiguration = new NetworkConfiguration
                {
                    AwsvpcConfiguration = new AwsVpcConfiguration
                    {
                        Subnets = subnets,
                        SecurityGroups = securityGroups,
                        AssignPublicIp = assignPublicIp ? AssignPublicIp.ENABLED : AssignPublicIp.DISABLED,
                    },
                },
                Tags = new List<Tag>
                {
                    new() { Key = "aura:layer", Value = layerName },
                    new() { Key = "aura:managed", Value = "true" },
                },
            }, ct);

            if (runResp.Failures.Count > 0)
            {
                var reasons = string.Join("; ", runResp.Failures.Select(f => $"{f.Arn ?? "?"}: {f.Reason}"));
                return new LayerExecutionResult(false, $"RunTask failures: {reasons}");
            }

            var taskArn = runResp.Tasks[0].TaskArn;
            _logger.LogInformation("Task {TaskArn} started — waiting up to {Timeout}s for STOPPED", taskArn, timeoutSeconds);

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5), ct);

                var desc = await ecs.DescribeTasksAsync(new DescribeTasksRequest
                {
                    Cluster = cluster,
                    Tasks = new List<string> { taskArn },
                }, ct);
                var task = desc.Tasks.FirstOrDefault();
                if (task is null) continue;

                if (task.LastStatus == "STOPPED")
                {
                    var container = task.Containers.FirstOrDefault();
                    var exitCode = container?.ExitCode;
                    var stoppedReason = task.StoppedReason ?? "(none)";
                    var success = exitCode == 0;
                    return new LayerExecutionResult(success,
                        $"Fargate task {taskArn} stopped: exitCode={exitCode}, reason={stoppedReason}");
                }
            }

            return new LayerExecutionResult(false, $"Fargate task {taskArn} did not reach STOPPED within {timeoutSeconds}s.");
        }
        catch (AmazonECSException ex)
        {
            _logger.LogError(ex, "Failed to run Fargate task on cluster {Cluster}", cluster);
            return new LayerExecutionResult(false, $"Failed to run Fargate task: {ex.ErrorCode} — {ex.Message}");
        }
    }
}
