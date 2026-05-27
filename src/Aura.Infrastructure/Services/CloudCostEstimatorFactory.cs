using Aura.Core.Enums;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

/// <summary>
/// Default factory — picks the matching ICloudCostEstimator from the DI
/// container's registered set. Falls back to Azure when no estimator is
/// registered for the requested provider so legacy single-provider behavior
/// is preserved.
/// </summary>
public class CloudCostEstimatorFactory : ICloudCostEstimatorFactory
{
    private readonly Dictionary<CloudProvider, ICloudCostEstimator> _estimators;
    private readonly ICloudCostEstimator _fallback;

    public CloudCostEstimatorFactory(IEnumerable<ICloudCostEstimator> estimators)
    {
        _estimators = estimators.ToDictionary(e => e.Provider);
        _fallback = _estimators.GetValueOrDefault(CloudProvider.Azure)
            ?? _estimators.Values.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No ICloudCostEstimator implementations registered.");
    }

    public ICloudCostEstimator For(CloudProvider provider) =>
        _estimators.TryGetValue(provider, out var estimator) ? estimator : _fallback;
}
