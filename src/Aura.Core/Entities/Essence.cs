namespace Aura.Core.Entities;

public class Essence : TenantScopedEntity
{
    public string Name { get; set; } = string.Empty;
    public Guid CloudAccountId { get; set; }
    public CloudAccount CloudAccount { get; set; } = null!;
    public string EssenceJson { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Deployment> Deployments { get; set; } = new List<Deployment>();
}
