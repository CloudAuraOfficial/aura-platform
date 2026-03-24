using System.Text.Json;
using Aura.Worker.Executors;

namespace Aura.Worker.Operations;

public interface IOperationHandler
{
    Task<LayerExecutionResult> ExecuteAsync(
        string layerName,
        JsonElement parameters,
        Dictionary<string, string> envVars,
        CancellationToken ct = default);
}
