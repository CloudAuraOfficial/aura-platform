using Aura.Core.Enums;

namespace Aura.Core.Entities;

public class Experiment : BaseEntity
{
    public string Project { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Hypothesis { get; set; } = string.Empty;
    public ExperimentStatus Status { get; set; } = ExperimentStatus.Draft;
    public string Variants { get; set; } = "[]";
    public string MetricName { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
    public DateTime? ConcludedAt { get; set; }
    public string? Conclusion { get; set; }

    public ICollection<ExperimentAssignment> Assignments { get; set; } = new List<ExperimentAssignment>();
    public ICollection<ExperimentEvent> Events { get; set; } = new List<ExperimentEvent>();
}
