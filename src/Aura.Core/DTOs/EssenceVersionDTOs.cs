namespace Aura.Core.DTOs;

public sealed record EssenceVersionResponse(
    Guid Id,
    int VersionNumber,
    string EssenceJson,
    Guid ChangedByUserId,
    DateTime CreatedAt
);

public sealed record EssenceDiffResponse(
    int FromVersion,
    int ToVersion,
    List<EssenceDiffEntry> Changes
);

public sealed record EssenceDiffEntry(
    string Path,
    string ChangeType, // "added", "removed", "modified"
    string? OldValue,
    string? NewValue
);
