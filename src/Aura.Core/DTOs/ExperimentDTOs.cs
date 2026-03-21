using System.ComponentModel.DataAnnotations;
using Aura.Core.Enums;

namespace Aura.Core.DTOs;

public sealed record CreateExperimentRequest(
    [Required, StringLength(100, MinimumLength = 1)] string Project,
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [Required, StringLength(2000, MinimumLength = 1)] string Hypothesis,
    [Required, StringLength(50000, MinimumLength = 2)] string Variants,
    [Required, StringLength(200, MinimumLength = 1)] string MetricName
);

public sealed record UpdateExperimentRequest(
    [StringLength(200)] string? Name,
    [StringLength(2000)] string? Hypothesis,
    ExperimentStatus? Status,
    [StringLength(2000)] string? Conclusion
);

public sealed record AssignVariantRequest(
    [Required, StringLength(500, MinimumLength = 1)] string SubjectKey
);

public sealed record TrackEventRequest(
    [Required, StringLength(200)] string VariantId,
    [Required, StringLength(200)] string SubjectHash,
    [Required, StringLength(200)] string MetricName,
    double MetricValue,
    string? Metadata
);

public sealed record ExperimentResponse(
    Guid Id,
    string Project,
    string Name,
    string Hypothesis,
    string Status,
    string Variants,
    string MetricName,
    DateTime? StartedAt,
    DateTime? ConcludedAt,
    string? Conclusion,
    DateTime CreatedAt
);

public sealed record ExperimentResultsResponse(
    Guid ExperimentId,
    string Name,
    string MetricName,
    Dictionary<string, VariantResultResponse> Variants,
    StatisticalSignificanceResponse? Significance
);

public sealed record VariantResultResponse(
    int SampleSize,
    double Mean,
    double StdDev,
    double Min,
    double Max
);

public sealed record StatisticalSignificanceResponse(
    double TStatistic,
    double PValue,
    int DegreesOfFreedom,
    bool IsSignificant,
    double ConfidenceLevel
);

public sealed record AssignVariantResponse(
    Guid ExperimentId,
    string VariantId,
    string SubjectHash
);
