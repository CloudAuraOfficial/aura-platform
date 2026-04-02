namespace Aura.Core.Entities;

public class AiGenerationLog : TenantScopedEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int Iterations { get; set; }
    public long DurationMs { get; set; }
    public bool Success { get; set; }
    public Guid? EssenceId { get; set; }
}
