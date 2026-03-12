namespace Aura.Api.Middleware;

public static class PaginationDefaults
{
    public const int MaxLimit = 100;
    public const int DefaultLimit = 25;

    public static (int offset, int limit) Clamp(int offset, int limit)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaxLimit);
        return (offset, limit);
    }
}
