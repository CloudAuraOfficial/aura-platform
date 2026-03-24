using System.Diagnostics;
using System.Text.Json;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class BuildContainerImageHandler : IOperationHandler
{
    private readonly ILogger<BuildContainerImageHandler> _logger;

    public BuildContainerImageHandler(ILogger<BuildContainerImageHandler> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("imageName", out var imageNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: imageName");

        if (!parameters.TryGetProperty("imageTag", out var imageTagProp))
            return new LayerExecutionResult(false, "Missing required parameter: imageTag");

        if (!parameters.TryGetProperty("dockerfilePath", out var dockerfilePathProp))
            return new LayerExecutionResult(false, "Missing required parameter: dockerfilePath");

        if (!parameters.TryGetProperty("registryName", out var registryNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: registryName");

        var imageName = imageNameProp.GetString()!;
        var imageTag = imageTagProp.GetString()!;
        var dockerfilePath = dockerfilePathProp.GetString()!;
        var registryName = registryNameProp.GetString()!;

        string? buildTarget = null;
        if (parameters.TryGetProperty("buildTarget", out var buildTargetProp))
            buildTarget = buildTargetProp.GetString();

        var fullImageTag = $"{registryName}.azurecr.io/{imageName}:{imageTag}";
        var args = $"build -t {fullImageTag}";

        if (!string.IsNullOrEmpty(buildTarget))
            args += $" --target {buildTarget}";

        args += $" {dockerfilePath}";

        _logger.LogInformation("Building container image: docker {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var (key, value) in envVars)
            psi.Environment[key] = value;

        return await RunProcessAsync(psi, ct);
    }

    private static async Task<LayerExecutionResult> RunProcessAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = string.IsNullOrEmpty(stderr)
            ? stdout
            : $"{stdout}\n--- STDERR ---\n{stderr}";

        return new LayerExecutionResult(process.ExitCode == 0, output.Trim());
    }
}
