using System.Diagnostics;
using System.Text;
using Aura.Core.Interfaces;
using Aura.Core.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class DockerContainerExecutionService : IContainerExecutionService, IDisposable
{
    private readonly DockerClient _docker;
    private readonly ILogStreamService _logStream;
    private readonly ILogger<DockerContainerExecutionService> _logger;
    private readonly string _memoryLimit;
    private readonly long _cpuLimit;
    private readonly TimeSpan _defaultTimeout;

    public DockerContainerExecutionService(
        ILogStreamService logStream,
        IConfiguration config,
        ILogger<DockerContainerExecutionService> logger)
    {
        _docker = new DockerClientConfiguration(
            new Uri("unix:///var/run/docker.sock")).CreateClient();
        _logStream = logStream;
        _logger = logger;
        _memoryLimit = config["EMISSIONLOAD_MEMORY_LIMIT"] ?? "512m";
        _cpuLimit = long.TryParse(config["EMISSIONLOAD_CPU_LIMIT_NANOCPUS"], out var cpu)
            ? cpu
            : 1_000_000_000; // 1 CPU
        _defaultTimeout = TimeSpan.FromSeconds(
            int.TryParse(config["EMISSIONLOAD_TIMEOUT_SECONDS"], out var t) ? t : 600);
    }

    public async Task<ContainerExecutionResult> ExecuteAsync(
        ContainerExecutionRequest request,
        CancellationToken ct = default)
    {
        var containerName = $"aura-run-{request.RunId:N}-{request.LayerName}"
            .ToLowerInvariant().Replace(' ', '-');
        var timeout = request.Timeout ?? _defaultTimeout;
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "Starting EmissionLoad container {ContainerName} with image {Image} for run {RunId} layer {LayerName}",
            containerName, request.ImageName, request.RunId, request.LayerName);

        try
        {
            // Build environment variables list
            var envVars = request.EnvVars
                .Select(kv => $"{kv.Key}={kv.Value}")
                .Concat(new[]
                {
                    $"AURA_RUN_ID={request.RunId}",
                    $"AURA_LAYER_ID={request.LayerId}",
                    $"AURA_LAYER_NAME={request.LayerName}",
                    $"AURA_PARAMETERS={request.Parameters}"
                })
                .ToList();

            if (!string.IsNullOrEmpty(request.OperationType))
                envVars.Add($"AURA_OPERATION_TYPE={request.OperationType}");

            var createParams = new CreateContainerParameters
            {
                Image = request.ImageName,
                Name = containerName,
                Env = envVars,
                HostConfig = new HostConfig
                {
                    Memory = ParseMemoryLimit(_memoryLimit),
                    NanoCPUs = _cpuLimit,
                    ReadonlyRootfs = false, // entrypoint needs /tmp for auth
                    AutoRemove = false, // we remove after reading logs
                    SecurityOpt = new List<string> { "no-new-privileges" }
                },
                Labels = new Dictionary<string, string>
                {
                    ["aura.run-id"] = request.RunId.ToString(),
                    ["aura.layer-id"] = request.LayerId.ToString(),
                    ["aura.managed"] = "true"
                }
            };

            // Create and start the container
            var createResponse = await _docker.Containers.CreateContainerAsync(createParams, ct);
            var containerId = createResponse.ID;

            _logger.LogInformation("Created container {ContainerId} for {ContainerName}", containerId, containerName);

            await _docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

            // Stream logs to Redis in real time
            var output = await StreamLogsAsync(containerId, request.RunId, request.LayerName, timeout, ct);

            // Wait for container to exit
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            ContainerWaitResponse waitResponse;
            try
            {
                waitResponse = await _docker.Containers.WaitContainerAsync(containerId, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — kill the container
                _logger.LogWarning("Container {ContainerId} timed out after {Timeout}, killing", containerId, timeout);
                await _docker.Containers.KillContainerAsync(containerId, new ContainerKillParameters(), CancellationToken.None);
                await CleanupContainerAsync(containerId);
                sw.Stop();

                return new ContainerExecutionResult(
                    Success: false,
                    Output: output + $"\n[EmissionLoad] Container killed after {timeout} timeout.",
                    ExitCode: -1,
                    Duration: sw.Elapsed);
            }

            // Final log read to catch any remaining output
            var finalLogs = await ReadRemainingLogsAsync(containerId);
            if (!string.IsNullOrEmpty(finalLogs))
                output += finalLogs;

            await CleanupContainerAsync(containerId);
            sw.Stop();

            var exitCode = (int)waitResponse.StatusCode;
            _logger.LogInformation(
                "Container {ContainerId} exited with code {ExitCode} in {Duration}ms",
                containerId, exitCode, sw.ElapsedMilliseconds);

            return new ContainerExecutionResult(
                Success: exitCode == 0,
                Output: output,
                ExitCode: exitCode,
                Duration: sw.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "EmissionLoad container execution failed for run {RunId} layer {LayerName}",
                request.RunId, request.LayerName);

            // Attempt cleanup
            try
            {
                var containers = await _docker.Containers.ListContainersAsync(
                    new ContainersListParameters { All = true }, CancellationToken.None);
                var match = containers.FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == containerName));
                if (match is not null)
                    await CleanupContainerAsync(match.ID);
            }
            catch { /* best effort */ }

            return new ContainerExecutionResult(
                Success: false,
                Output: $"[EmissionLoad] Container execution error: {ex.Message}",
                ExitCode: -1,
                Duration: sw.Elapsed);
        }
    }

    private async Task<string> StreamLogsAsync(
        string containerId, Guid runId, string layerName, TimeSpan timeout, CancellationToken ct)
    {
        var output = new StringBuilder();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var logParams = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Follow = true,
                Timestamps = false
            };

            using var logStream = await _docker.Containers.GetContainerLogsAsync(
                containerId, false, logParams, timeoutCts.Token);

            var buffer = new byte[8192];
            while (true)
            {
                var result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, timeoutCts.Token);
                if (result.Count == 0)
                    break;

                var line = Encoding.UTF8.GetString(buffer, 0, result.Count).TrimEnd('\n', '\r');
                if (string.IsNullOrEmpty(line))
                    continue;

                output.AppendLine(line);
                await _logStream.PublishAsync(runId, $"[{layerName}] {line}", ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when container exits or timeout
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Log streaming interrupted for container {ContainerId}", containerId);
        }

        return output.ToString();
    }

    private async Task<string> ReadRemainingLogsAsync(string containerId)
    {
        try
        {
            var logParams = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Follow = false,
                Tail = "50"
            };

            using var logStream = await _docker.Containers.GetContainerLogsAsync(
                containerId, false, logParams, CancellationToken.None);

            var buffer = new byte[8192];
            var output = new StringBuilder();
            while (true)
            {
                var result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
                if (result.Count == 0)
                    break;
                output.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }

            return output.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task CleanupContainerAsync(string containerId)
    {
        try
        {
            await _docker.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            _logger.LogDebug("Removed container {ContainerId}", containerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove container {ContainerId}", containerId);
        }
    }

    private static long ParseMemoryLimit(string limit)
    {
        limit = limit.Trim().ToLowerInvariant();
        if (limit.EndsWith('g'))
            return long.Parse(limit[..^1]) * 1024 * 1024 * 1024;
        if (limit.EndsWith('m'))
            return long.Parse(limit[..^1]) * 1024 * 1024;
        return long.Parse(limit);
    }

    public void Dispose()
    {
        _docker.Dispose();
        GC.SuppressFinalize(this);
    }
}
