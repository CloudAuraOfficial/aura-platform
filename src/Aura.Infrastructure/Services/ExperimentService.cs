using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        return results;
    }

    private static string ComputeHash(string input)
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

    private sealed class VariantConfig
    {
        public string Id { get; set; } = string.Empty;
        public int Weight { get; set; }
        public JsonElement? Config { get; set; }
    }
}
