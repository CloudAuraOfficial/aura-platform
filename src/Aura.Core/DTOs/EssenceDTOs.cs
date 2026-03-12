using System.ComponentModel.DataAnnotations;

namespace Aura.Core.DTOs;

public sealed record CreateEssenceRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required] Guid CloudAccountId,
    [Required, StringLength(500000, MinimumLength = 2)] string EssenceJson
);

public sealed record UpdateEssenceRequest(
    [StringLength(200)] string? Name,
    Guid? CloudAccountId,
    [StringLength(500000)] string? EssenceJson
);

public sealed record CloneEssenceRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name
);

public sealed record EssenceResponse(
    Guid Id,
    string Name,
    Guid CloudAccountId,
    string EssenceJson,
    int CurrentVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
