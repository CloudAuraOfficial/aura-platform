using Aura.Core.Enums;

namespace Aura.Core.Models;

public sealed record ContainerExecutionRequest(
    Guid RunId,
    Guid LayerId,
    string ImageName,
    string EssenceJson,
    string LayerName,
    string? OperationType,
    string Parameters,
    Dictionary<string, string> EnvVars,
    TimeSpan? Timeout = null);

public sealed record ContainerExecutionResult(
    bool Success,
    string Output,
    int ExitCode,
    TimeSpan Duration);

public sealed record EmissionLoadConfig(
    string ImageName,
    string ImageTag,
    CloudProvider Provider,
    string TenantSlug);
