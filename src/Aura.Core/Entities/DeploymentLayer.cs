using Aura.Core.Enums;

namespace Aura.Core.Entities;

public class DeploymentLayer : BaseEntity
{
    public Guid RunId { get; set; }
    public DeploymentRun Run { get; set; } = null!;
    public string LayerName { get; set; } = string.Empty;
    public ExecutorType ExecutorType { get; set; }
    public LayerStatus Status { get; set; } = LayerStatus.Pending;
    public string Parameters { get; set; } = "{}";
    public string? ScriptPath { get; set; }
    public string? OperationType { get; set; }
    public string DependsOn { get; set; } = "[]";
    public int SortOrder { get; set; }
    public string? Output { get; set; }
    public string? EmissionLoadImage { get; set; }

    // Per-layer cloud-account override (Epic 3). When null, the layer
    // inherits the Essence-level CloudAccount. Enables single Essences
    // that span multiple subscriptions or multiple cloud providers.
    public Guid? CloudAccountId { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
