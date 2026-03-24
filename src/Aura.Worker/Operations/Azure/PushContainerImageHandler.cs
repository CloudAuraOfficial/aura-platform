using System.Diagnostics;
using System.Text.Json;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class PushContainerImageHandler : IOperationHandler
{
    private readonly ILogger<PushContainerImageHandler> _logger;

    public PushContainerImageHandler(ILogger<PushContainerImageHandler> logger)
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

        if (!parameters.TryGetProperty("registryName", out var registryNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: registryName");

        var imageName = imageNameProp.GetString()!;
        var imageTag = imageTagProp.GetString()!;
        var registryName = registryNameProp.GetString()!;

        var fullImageTag = $"{registryName}.azurecr.io/{imageName}:{imageTag}";
        var command = $"az acr login --name {registryName} && docker push {fullImageTag}";

        _logger.LogInformation("Pushing container image: {Command}", command);

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{command}\"",
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
            ?? throw new InvalidOperationException("Failed to start process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = string.IsNullOrEmpty(stderr)
            ? stdout
            : $"{stdout}\n--- STDERR ---\n{stderr}";

        return new LayerExecutionResult(process.ExitCode == 0, output.Trim());
    }
}
