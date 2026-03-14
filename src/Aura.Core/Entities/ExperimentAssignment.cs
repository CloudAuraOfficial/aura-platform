namespace Aura.Core.Entities;

public class ExperimentAssignment : BaseEntity
{
    public Guid ExperimentId { get; set; }
    public Experiment Experiment { get; set; } = null!;
    public string SubjectHash { get; set; } = string.Empty;
    public string VariantId { get; set; } = string.Empty;
}
