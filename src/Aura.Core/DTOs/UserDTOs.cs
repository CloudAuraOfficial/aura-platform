using Aura.Core.Enums;

namespace Aura.Core.DTOs;

public sealed record CreateUserRequest(string Email, string Password, UserRole Role = UserRole.Member);

public sealed record UpdateUserRequest(string? Email, UserRole? Role);

public sealed record UserResponse(
    Guid Id,
    string Email,
    string Role,
    DateTime CreatedAt
);
