using System.ComponentModel.DataAnnotations;

namespace Aura.Core.DTOs;

public sealed record CreateDeploymentRequest(
    [Required] Guid EssenceId,
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [StringLength(100)] string? CronExpression,
    [Url, StringLength(2000)] string? WebhookUrl,
    bool IsEnabled = true
);

public sealed record UpdateDeploymentRequest(
    [StringLength(200)] string? Name,
    [StringLength(100)] string? CronExpression,
    [Url, StringLength(2000)] string? WebhookUrl,
    bool? IsEnabled
);

public sealed record DeploymentResponse(
    Guid Id,
    Guid EssenceId,
    string Name,
    string? CronExpression,
    string? WebhookUrl,
    bool IsEnabled,
    DateTime CreatedAt,
    LatestRunSummary? LatestRun = null
);

/// <summary>Slim latest-run projection embedded in deployment list items
/// so the dashboard doesn't need a per-deployment runs query.</summary>
public sealed record LatestRunSummary(
    Guid Id,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt
);

/// <summary>Result of checking a deployment's essence against what run creation and save accept.
/// Errors is empty when valid; Message is a one-line summary for display.</summary>
public sealed record DeploymentValidationResponse(
    bool IsValid,
    string Message,
    IReadOnlyList<string> Errors
);
