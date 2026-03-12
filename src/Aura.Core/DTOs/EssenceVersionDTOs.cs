namespace Aura.Core.DTOs;

public sealed record EssenceVersionResponse(
    Guid Id,
    int VersionNumber,
    string EssenceJson,
    Guid ChangedByUserId,
    DateTime CreatedAt
);
