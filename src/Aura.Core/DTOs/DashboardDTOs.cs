namespace Aura.Core.DTOs;

public sealed record DashboardStatsResponse(
    int EssenceCount,
    int DeploymentCount,
    int UserCount,
    int CloudAccountCount,
    decimal TotalCostUsd
);

public sealed record RecentRunResponse(
    Guid Id,
    Guid DeploymentId,
    string DeploymentName,
    string Status,
    int LayerCount,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime CreatedAt,
    decimal? EstimatedCostUsd
);

public sealed record RecentEssenceResponse(
    Guid Id,
    string Name,
    string Provider,
    int LayerCount,
    int CurrentVersion,
    DateTime UpdatedAt
);
