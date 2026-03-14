using Aura.Core.Entities;

namespace Aura.Core.Interfaces;

public interface IExperimentService
{
    Task<List<Experiment>> GetActiveAsync(string project, CancellationToken ct = default);
    Task<string> AssignVariantAsync(Guid experimentId, string subjectKey, CancellationToken ct = default);
    Task TrackEventAsync(Guid experimentId, string variantId, string subjectHash, string metricName, double metricValue, string? metadata = null, CancellationToken ct = default);
    Task<ExperimentResults> GetResultsAsync(Guid experimentId, CancellationToken ct = default);
}

public class ExperimentResults
{
    public Guid ExperimentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MetricName { get; set; } = string.Empty;
    public Dictionary<string, VariantResult> Variants { get; set; } = new();
}

public class VariantResult
{
    public int SampleSize { get; set; }
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}
