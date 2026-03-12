using System.ComponentModel.DataAnnotations;

namespace Aura.Core.DTOs;

public sealed record LoginRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password
);

public sealed record LoginResponse(string Token, string RefreshToken, DateTime ExpiresAt);

public sealed record RefreshRequest(
    [Required] string RefreshToken
);

public sealed record BootstrapRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [Required, StringLength(100, MinimumLength = 2)] string TenantName
);
