using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class ExperimentService : IExperimentService
{
    private readonly AuraDbContext _db;
    private readonly ILogger<ExperimentService> _logger;

    public ExperimentService(AuraDbContext db, ILogger<ExperimentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Experiment> CreateAsync(string project, string name, string hypothesis,
        string variants, string metricName, CancellationToken ct = default)
    {
        // Validate variants JSON
        var parsed = JsonSerializer.Deserialize<List<VariantConfig>>(variants)
            ?? throw new ArgumentException("Variants must be a valid JSON array");

        if (parsed.Count < 2)
            throw new ArgumentException("At least two variants are required");

        if (parsed.Sum(v => v.Weight) != 100)
            throw new ArgumentException("Variant weights must sum to 100");

        var experiment = new Experiment
        {
            Project = project,
            Name = name,
            Hypothesis = hypothesis,
            Variants = variants,
            MetricName = metricName,
            Status = ExperimentStatus.Draft
        };

        _db.Experiments.Add(experiment);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created experiment {ExperimentId} ({Name}) for project {Project}",
            experiment.Id, name, project);
        return experiment;
    }

    public async Task<Experiment?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Experiments.FindAsync(new object[] { id }, ct);
    }

    public async Task<(List<Experiment> Items, int Total)> ListAsync(
        string? project, ExperimentStatus? status, int offset, int limit, CancellationToken ct = default)
    {
        var query = _db.Experiments.AsQueryable();

        if (!string.IsNullOrEmpty(project))
            query = query.Where(e => e.Project == project);

        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);

        query = query.OrderByDescending(e => e.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query.Skip(offset).Take(limit).ToListAsync(ct);

        return (items, total);
    }

    public async Task<Experiment> UpdateAsync(Guid id, string? name, string? hypothesis,
        ExperimentStatus? status, string? conclusion, CancellationToken ct = default)
    {
        var experiment = await _db.Experiments.FindAsync(new object[] { id }, ct)
            ?? throw new InvalidOperationException($"Experiment {id} not found");

        if (name is not null)
            experiment.Name = name;

        if (hypothesis is not null)
            experiment.Hypothesis = hypothesis;

        if (conclusion is not null)
            experiment.Conclusion = conclusion;

        if (status.HasValue && status.Value != experiment.Status)
        {
            ValidateStatusTransition(experiment.Status, status.Value);

            experiment.Status = status.Value;

            if (status.Value == ExperimentStatus.Running && experiment.StartedAt is null)
                experiment.StartedAt = DateTime.UtcNow;

            if (status.Value == ExperimentStatus.Concluded)
                experiment.ConcludedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Updated experiment {ExperimentId} (status={Status})",
            id, experiment.Status);
        return experiment;
    }

    public async Task<List<Experiment>> GetActiveAsync(string project, CancellationToken ct = default)
    {
        return await _db.Experiments
            .Where(e => e.Project == project && e.Status == ExperimentStatus.Running)
            .ToListAsync(ct);
    }

    public async Task<string> AssignVariantAsync(Guid experimentId, string subjectKey, CancellationToken ct = default)
    {
        var subjectHash = ComputeHash($"{experimentId}:{subjectKey}");

        var existing = await _db.ExperimentAssignments
            .FirstOrDefaultAsync(a => a.ExperimentId == experimentId && a.SubjectHash == subjectHash, ct);

        if (existing != null)
            return existing.VariantId;

        var experiment = await _db.Experiments.FindAsync(new object[] { experimentId }, ct)
            ?? throw new InvalidOperationException($"Experiment {experimentId} not found");

        var variants = JsonSerializer.Deserialize<List<VariantConfig>>(experiment.Variants)
            ?? throw new InvalidOperationException("Invalid variants configuration");

        var variantId = PickVariant(subjectHash, variants);

        _db.ExperimentAssignments.Add(new ExperimentAssignment
        {
            ExperimentId = experimentId,
            SubjectHash = subjectHash,
            VariantId = variantId
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Assigned variant {VariantId} for experiment {ExperimentId}", variantId, experimentId);
        return variantId;
    }

    public async Task TrackEventAsync(Guid experimentId, string variantId, string subjectHash,
        string metricName, double metricValue, string? metadata = null, CancellationToken ct = default)
    {
        _db.ExperimentEvents.Add(new ExperimentEvent
        {
            ExperimentId = experimentId,
            VariantId = variantId,
            SubjectHash = subjectHash,
            MetricName = metricName,
            MetricValue = metricValue,
            Metadata = metadata
        });

        await _db.SaveChangesAsync(ct);
    }

    public async Task<ExperimentResults> GetResultsAsync(Guid experimentId, CancellationToken ct = default)
    {
        var experiment = await _db.Experiments.FindAsync(new object[] { experimentId }, ct)
            ?? throw new InvalidOperationException($"Experiment {experimentId} not found");

        var events = await _db.ExperimentEvents
            .Where(e => e.ExperimentId == experimentId && e.MetricName == experiment.MetricName)
            .ToListAsync(ct);

        var results = new ExperimentResults
        {
            ExperimentId = experimentId,
            Name = experiment.Name,
            MetricName = experiment.MetricName
        };

        foreach (var group in events.GroupBy(e => e.VariantId))
        {
            var values = group.Select(e => e.MetricValue).ToList();
            var mean = values.Average();
            var stdDev = values.Count > 1
                ? Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / (values.Count - 1))
                : 0;

            results.Variants[group.Key] = new VariantResult
            {
                SampleSize = values.Count,
                Mean = mean,
                StdDev = stdDev,
                Min = values.Min(),
                Max = values.Max()
            };
        }

        // Compute statistical significance if exactly 2 variants with sufficient data
        if (results.Variants.Count == 2)
        {
            var variants = results.Variants.Values.ToList();
            if (variants[0].SampleSize >= 2 && variants[1].SampleSize >= 2)
            {
                results.Significance = ComputeWelchTTest(variants[0], variants[1]);
            }
        }

        return results;
    }

    private static StatisticalSignificance ComputeWelchTTest(VariantResult a, VariantResult b)
    {
        var n1 = a.SampleSize;
        var n2 = b.SampleSize;
        var s1Sq = a.StdDev * a.StdDev;
        var s2Sq = b.StdDev * b.StdDev;

        var seSum = s1Sq / n1 + s2Sq / n2;

        // Guard against zero variance
        if (seSum < 1e-15)
        {
            return new StatisticalSignificance
            {
                TStatistic = 0,
                PValue = 1.0,
                DegreesOfFreedom = n1 + n2 - 2,
                IsSignificant = false
            };
        }

        var t = (a.Mean - b.Mean) / Math.Sqrt(seSum);

        // Welch-Satterthwaite degrees of freedom
        var dfNum = seSum * seSum;
        var dfDen = (s1Sq / n1) * (s1Sq / n1) / (n1 - 1) +
                    (s2Sq / n2) * (s2Sq / n2) / (n2 - 1);
        var df = (int)Math.Floor(dfNum / dfDen);
        df = Math.Max(df, 1);

        // Two-tailed p-value using regularized incomplete beta function
        var pValue = 2.0 * StudentTCdf(-Math.Abs(t), df);

        return new StatisticalSignificance
        {
            TStatistic = Math.Round(t, 6),
            PValue = Math.Round(pValue, 6),
            DegreesOfFreedom = df,
            IsSignificant = pValue < 0.05
        };
    }

    /// <summary>
    /// Student's t-distribution CDF using the regularized incomplete beta function.
    /// </summary>
    private static double StudentTCdf(double t, int df)
    {
        var x = df / (df + t * t);
        var beta = RegularizedIncompleteBeta(df / 2.0, 0.5, x);
        return 0.5 * beta;
    }

    /// <summary>
    /// Regularized incomplete beta function I_x(a, b) using continued fraction approximation.
    /// Abramowitz and Stegun, 26.5.8.
    /// </summary>
    private static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x < 0 || x > 1) return 0;
        if (x == 0) return 0;
        if (x == 1) return 1;

        // Use symmetry relation when x > (a+1)/(a+b+2)
        if (x > (a + 1) / (a + b + 2))
            return 1.0 - RegularizedIncompleteBeta(b, a, 1.0 - x);

        var lnBeta = LogBeta(a, b);
        var front = Math.Exp(a * Math.Log(x) + b * Math.Log(1.0 - x) - lnBeta) / a;

        // Lentz's continued fraction
        var f = 1.0;
        var c = 1.0;
        var d = 1.0 - (a + b) * x / (a + 1.0);
        if (Math.Abs(d) < 1e-30) d = 1e-30;
        d = 1.0 / d;
        f = d;

        for (int m = 1; m <= 200; m++)
        {
            // Even step
            var numerator = m * (b - m) * x / ((a + 2.0 * m - 1.0) * (a + 2.0 * m));
            d = 1.0 + numerator * d;
            if (Math.Abs(d) < 1e-30) d = 1e-30;
            c = 1.0 + numerator / c;
            if (Math.Abs(c) < 1e-30) c = 1e-30;
            d = 1.0 / d;
            f *= d * c;

            // Odd step
            numerator = -(a + m) * (a + b + m) * x / ((a + 2.0 * m) * (a + 2.0 * m + 1.0));
            d = 1.0 + numerator * d;
            if (Math.Abs(d) < 1e-30) d = 1e-30;
            c = 1.0 + numerator / c;
            if (Math.Abs(c) < 1e-30) c = 1e-30;
            d = 1.0 / d;
            var delta = d * c;
            f *= delta;

            if (Math.Abs(delta - 1.0) < 1e-10)
                break;
        }

        return front * f;
    }

    private static double LogBeta(double a, double b)
    {
        return LogGamma(a) + LogGamma(b) - LogGamma(a + b);
    }

    /// <summary>
    /// Stirling's approximation for ln(Gamma(x)).
    /// </summary>
    private static double LogGamma(double x)
    {
        // Lanczos approximation coefficients
        double[] c = {
            76.18009172947146,
            -86.50532032941677,
            24.01409824083091,
            -1.231739572450155,
            0.1208650973866179e-2,
            -0.5395239384953e-5
        };

        var y = x;
        var tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        var ser = 1.000000000190015;
        for (int j = 0; j < 6; j++)
        {
            y += 1;
            ser += c[j] / y;
        }
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }

    private static void ValidateStatusTransition(ExperimentStatus from, ExperimentStatus to)
    {
        var valid = (from, to) switch
        {
            (ExperimentStatus.Draft, ExperimentStatus.Running) => true,
            (ExperimentStatus.Running, ExperimentStatus.Paused) => true,
            (ExperimentStatus.Running, ExperimentStatus.Concluded) => true,
            (ExperimentStatus.Paused, ExperimentStatus.Running) => true,
            (ExperimentStatus.Paused, ExperimentStatus.Concluded) => true,
            _ => false
        };

        if (!valid)
            throw new InvalidOperationException(
                $"Invalid status transition: {from} → {to}");
    }

    public static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string PickVariant(string subjectHash, List<VariantConfig> variants)
    {
        var hashInt = int.Parse(subjectHash[..8], System.Globalization.NumberStyles.HexNumber);
        var bucket = Math.Abs(hashInt) % 100;
        var cumulative = 0;

        foreach (var v in variants)
        {
            cumulative += v.Weight;
            if (bucket < cumulative)
                return v.Id;
        }

        return variants.Last().Id;
    }

    internal sealed class VariantConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        [JsonPropertyName("weight")]
        public int Weight { get; set; }
        [JsonPropertyName("config")]
        public JsonElement? Config { get; set; }
    }
}
