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
    public string DependsOn { get; set; } = "[]";
    public int SortOrder { get; set; }
    public string? Output { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
