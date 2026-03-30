namespace Aura.Core.DTOs;

public sealed record DeploymentRunResponse(
    Guid Id,
    Guid DeploymentId,
    string Status,
    string SnapshotJson,
    IReadOnlyList<DeploymentLayerResponse> Layers,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime CreatedAt
);

public sealed record DeploymentLayerResponse(
    Guid Id,
    string LayerName,
    string ExecutorType,
    string Status,
    string Parameters,
    string? ScriptPath,
    string DependsOn,
    int SortOrder,
    string? Output,
    string? EmissionLoadImage,
    DateTime? StartedAt,
    DateTime? CompletedAt
);
