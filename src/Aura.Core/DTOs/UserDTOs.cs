using System.ComponentModel.DataAnnotations;
using Aura.Core.Enums;

namespace Aura.Core.DTOs;

public sealed record CreateUserRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    UserRole Role = UserRole.Member
);

public sealed record UpdateUserRequest(
    [EmailAddress, StringLength(256)] string? Email,
    UserRole? Role,
    bool? IsDisabled
);

public sealed record InviteUserRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    UserRole Role = UserRole.Member
);

public sealed record AcceptInviteRequest(
    [Required] string InviteToken,
    [Required, StringLength(128, MinimumLength = 8)] string Password
);

public sealed record UserResponse(
    Guid Id,
    string Email,
    string Role,
    bool IsDisabled,
    DateTime CreatedAt
);
