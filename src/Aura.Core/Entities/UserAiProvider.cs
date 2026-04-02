namespace Aura.Core.Entities;

public class UserAiProvider : TenantScopedEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string ProviderName { get; set; } = string.Empty;
    public string EncryptedApiKey { get; set; } = string.Empty;
    public string? DisplayLabel { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
