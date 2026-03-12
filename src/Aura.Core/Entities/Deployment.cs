namespace Aura.Core.Entities;

public class Deployment : TenantScopedEntity
{
    public Guid EssenceId { get; set; }
    public Essence Essence { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? CronExpression { get; set; }
    public string? WebhookUrl { get; set; }
    public bool IsEnabled { get; set; } = true;

    public ICollection<DeploymentRun> Runs { get; set; } = new List<DeploymentRun>();
}
