using System.Diagnostics;
using Aura.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Executors;

public class CSharpSdkExecutor : ILayerExecutor
{
    private readonly ILogger<CSharpSdkExecutor> _logger;

    public CSharpSdkExecutor(ILogger<CSharpSdkExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        DeploymentLayer layer, string workDir, Dictionary<string, string> envVars, CancellationToken ct)
    {
        // Step 1: dotnet build
        var buildResult = await RunDotnetAsync("build", workDir, envVars, ct);
        if (!buildResult.Success)
            return new LayerExecutionResult(false, $"Build failed:\n{buildResult.Output}");

        // Step 2: dotnet run with AURA_CSHARP_CLASS and AURA_CSHARP_METHOD env vars
        var runEnv = new Dictionary<string, string>(envVars)
        {
            ["AURA_PARAMETERS"] = layer.Parameters,
            ["AURA_CSHARP_CLASS"] = layer.LayerName,
            ["AURA_CSHARP_METHOD"] = "Execute"
        };

        _logger.LogInformation("Executing C# SDK: class={Class}, method=Execute", layer.LayerName);

        var runResult = await RunDotnetAsync("run", workDir, runEnv, ct);
        return runResult;
    }

    private static async Task<LayerExecutionResult> RunDotnetAsync(
        string command, string workDir, Dictionary<string, string> envVars, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = command,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var (key, value) in envVars)
            psi.Environment[key] = value;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start dotnet {command}.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = string.IsNullOrEmpty(stderr)
            ? stdout
            : $"{stdout}\n--- STDERR ---\n{stderr}";

        return new LayerExecutionResult(process.ExitCode == 0, output.Trim());
    }
}
