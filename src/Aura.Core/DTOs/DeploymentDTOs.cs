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
    DateTime CreatedAt
);
