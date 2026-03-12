namespace Aura.Core.DTOs;

public sealed record CreateDeploymentRequest(
    Guid EssenceId,
    string Name,
    string? CronExpression,
    string? WebhookUrl,
    bool IsEnabled = true
);

public sealed record UpdateDeploymentRequest(
    string? Name,
    string? CronExpression,
    string? WebhookUrl,
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
