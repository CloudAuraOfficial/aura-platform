namespace Aura.Core.DTOs;

public sealed record PaginatedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Offset,
    int Limit
);
