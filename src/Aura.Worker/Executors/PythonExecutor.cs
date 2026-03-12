using System.Diagnostics;
using System.Text.Json;
using Aura.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Executors;

public class PythonExecutor : ILayerExecutor
{
    private readonly ILogger<PythonExecutor> _logger;

    public PythonExecutor(ILogger<PythonExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        DeploymentLayer layer, string workDir, Dictionary<string, string> envVars, CancellationToken ct)
    {
        var scriptPath = layer.ScriptPath;
        if (string.IsNullOrEmpty(scriptPath))
            return new LayerExecutionResult(false, "No scriptPath specified for Python layer.");

        var fullPath = Path.Combine(workDir, scriptPath);
        if (!File.Exists(fullPath))
            return new LayerExecutionResult(false, $"Script not found: {scriptPath}");

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = $"\"{fullPath}\"",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Inject all env vars (BYOS credentials, etc.)
        foreach (var (key, value) in envVars)
            psi.Environment[key] = value;

        // AURA_PARAMETERS as full JSON + individual AURA_PARAM_* vars
        psi.Environment["AURA_PARAMETERS"] = layer.Parameters;

        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(layer.Parameters);
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
                psi.Environment[$"AURA_PARAM_{key.ToUpperInvariant()}"] = value.ToString();
        }

        _logger.LogInformation("Executing Python: python3 {Script}", scriptPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start python3 process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = string.IsNullOrEmpty(stderr)
            ? stdout
            : $"{stdout}\n--- STDERR ---\n{stderr}";

        return new LayerExecutionResult(process.ExitCode == 0, output.Trim());
    }
}
