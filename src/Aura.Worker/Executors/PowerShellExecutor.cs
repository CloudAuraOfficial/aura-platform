using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aura.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Executors;

public class PowerShellExecutor : ILayerExecutor
{
    private readonly ILogger<PowerShellExecutor> _logger;

    public PowerShellExecutor(ILogger<PowerShellExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        DeploymentLayer layer, string workDir, Dictionary<string, string> envVars, CancellationToken ct)
    {
        var scriptPath = layer.ScriptPath;
        if (string.IsNullOrEmpty(scriptPath))
            return new LayerExecutionResult(false, "No scriptPath specified for PowerShell layer.");

        var fullPath = Path.Combine(workDir, scriptPath);
        if (!File.Exists(fullPath))
            return new LayerExecutionResult(false, $"Script not found: {scriptPath}");

        // Build -Key Value args from parameters
        var args = new StringBuilder($"-NoProfile -NonInteractive -File \"{fullPath}\"");
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(layer.Parameters);
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                args.Append($" -{key} \"{value}\"");
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = args.ToString(),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Inject AURA_PARAM_* env vars
        foreach (var (key, value) in envVars)
            psi.Environment[key] = value;

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
                psi.Environment[$"AURA_PARAM_{key.ToUpperInvariant()}"] = value.ToString();
        }

        _logger.LogInformation("Executing PowerShell: pwsh {Args}", args);

        return await RunProcessAsync(psi, ct);
    }

    private static async Task<LayerExecutionResult> RunProcessAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = string.IsNullOrEmpty(stderr)
            ? stdout
            : $"{stdout}\n--- STDERR ---\n{stderr}";

        return new LayerExecutionResult(process.ExitCode == 0, output.Trim());
    }
}
