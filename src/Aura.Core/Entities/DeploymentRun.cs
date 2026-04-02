using Aura.Core.Enums;

namespace Aura.Core.Entities;

public class DeploymentRun : TenantScopedEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public string SnapshotJson { get; set; } = "{}";
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Estimated cloud cost (USD) for this run, computed and frozen when the run reaches a terminal state.
    /// </summary>
    public decimal? EstimatedCostUsd { get; set; }

    /// <summary>
    /// W3C traceparent header for distributed trace propagation across the DB queue boundary.
    /// </summary>
    public string? TraceParent { get; set; }

    public ICollection<DeploymentLayer> Layers { get; set; } = new List<DeploymentLayer>();
}
