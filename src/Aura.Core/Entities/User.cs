using Aura.Core.Enums;

namespace Aura.Core.Entities;

public class User : TenantScopedEntity
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Member;
    public bool IsDisabled { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }
}
