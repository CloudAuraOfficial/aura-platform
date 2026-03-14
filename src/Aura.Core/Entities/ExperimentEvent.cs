namespace Aura.Core.Entities;

public class ExperimentEvent : BaseEntity
{
    public Guid ExperimentId { get; set; }
    public Experiment Experiment { get; set; } = null!;
    public string VariantId { get; set; } = string.Empty;
    public string SubjectHash { get; set; } = string.Empty;
    public string MetricName { get; set; } = string.Empty;
    public double MetricValue { get; set; }
    public string? Metadata { get; set; }
}
