using Aura.Core.Entities;
using Aura.Core.Enums;

namespace Aura.Core.Interfaces;

public interface IExperimentService
{
    Task<Experiment> CreateAsync(string project, string name, string hypothesis, string variants, string metricName, CancellationToken ct = default);
    Task<Experiment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<(List<Experiment> Items, int Total)> ListAsync(string? project, ExperimentStatus? status, int offset, int limit, CancellationToken ct = default);
    Task<Experiment> UpdateAsync(Guid id, string? name, string? hypothesis, ExperimentStatus? status, string? conclusion, CancellationToken ct = default);
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
    public StatisticalSignificance? Significance { get; set; }
}

public class VariantResult
{
    public int SampleSize { get; set; }
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}

public class StatisticalSignificance
{
    public double TStatistic { get; set; }
    public double PValue { get; set; }
    public int DegreesOfFreedom { get; set; }
    public bool IsSignificant { get; set; }
    public double ConfidenceLevel { get; set; } = 0.95;
}
