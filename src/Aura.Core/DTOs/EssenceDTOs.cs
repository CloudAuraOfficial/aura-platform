namespace Aura.Core.DTOs;

public sealed record CreateEssenceRequest(
    string Name,
    Guid CloudAccountId,
    string EssenceJson
);

public sealed record UpdateEssenceRequest(string? Name, Guid? CloudAccountId, string? EssenceJson);

public sealed record CloneEssenceRequest(string Name);

public sealed record EssenceResponse(
    Guid Id,
    string Name,
    Guid CloudAccountId,
    string EssenceJson,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
