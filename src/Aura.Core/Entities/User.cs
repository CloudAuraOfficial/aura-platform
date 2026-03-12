using Aura.Core.Enums;

namespace Aura.Core.Entities;

public class User : TenantScopedEntity
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Member;
}
