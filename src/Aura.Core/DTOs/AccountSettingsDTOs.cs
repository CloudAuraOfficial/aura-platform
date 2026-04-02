using System.ComponentModel.DataAnnotations;

namespace Aura.Core.DTOs;

public sealed record AiProviderResponse(
    Guid Id,
    string ProviderName,
    string? DisplayLabel,
    bool HasKey,
    DateTime UpdatedAt
);

public sealed record CreateAiProviderRequest(
    [Required][MaxLength(50)] string ProviderName,
    [Required][MinLength(1)] string ApiKey,
    [MaxLength(200)] string? DisplayLabel
);

public sealed record UpdateAiProviderRequest(
    [Required][MinLength(1)] string ApiKey,
    [MaxLength(200)] string? DisplayLabel
);

public sealed record TestAiProviderResponse(
    bool Success,
    string? Error
);
