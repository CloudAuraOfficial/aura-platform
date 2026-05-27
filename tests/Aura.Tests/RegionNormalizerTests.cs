using Aura.Core.Enums;
using Aura.Core.Utilities;
using Xunit;

namespace Aura.Tests;

public class RegionNormalizerTests
{
    [Theory]
    [InlineData(CloudProvider.Azure, "us-east", "eastus")]
    [InlineData(CloudProvider.Aws,   "us-east", "us-east-1")]
    [InlineData(CloudProvider.Gcp,   "us-east", "us-east1")]
    [InlineData(CloudProvider.Azure, "eu-west", "westeurope")]
    [InlineData(CloudProvider.Aws,   "eu-west", "eu-west-1")]
    [InlineData(CloudProvider.Gcp,   "eu-west", "europe-west1")]
    public void ToProvider_KnownCanonical_ReturnsSlug(CloudProvider provider, string canonical, string expected)
    {
        Assert.Equal(expected, RegionNormalizer.ToProvider(provider, canonical));
    }

    [Fact]
    public void ToProvider_UnknownCanonical_ReturnsNull()
    {
        Assert.Null(RegionNormalizer.ToProvider(CloudProvider.Azure, "mars-orbit"));
    }

    [Theory]
    [InlineData(CloudProvider.Azure, "eastus",      "us-east")]
    [InlineData(CloudProvider.Aws,   "us-east-1",   "us-east")]
    [InlineData(CloudProvider.Gcp,   "us-east1",    "us-east")]
    [InlineData(CloudProvider.Azure, "EASTUS",      "us-east")]
    public void ToCanonical_KnownSlug_ReturnsCanonical(CloudProvider provider, string slug, string expected)
    {
        Assert.Equal(expected, RegionNormalizer.ToCanonical(provider, slug));
    }

    [Fact]
    public void ToCanonical_UnknownSlug_ReturnsNull()
    {
        Assert.Null(RegionNormalizer.ToCanonical(CloudProvider.Aws, "fake-region-1"));
    }

    [Fact]
    public void SameGeo_AzureAndAwsInUsEast_True()
    {
        Assert.True(RegionNormalizer.SameGeo(
            CloudProvider.Azure, "eastus",
            CloudProvider.Aws,   "us-east-1"));
    }

    [Fact]
    public void SameGeo_AzureUsEastVsGcpEuWest_False()
    {
        Assert.False(RegionNormalizer.SameGeo(
            CloudProvider.Azure, "eastus",
            CloudProvider.Gcp,   "europe-west1"));
    }

    [Fact]
    public void SameGeo_UnknownRegion_False()
    {
        Assert.False(RegionNormalizer.SameGeo(
            CloudProvider.Azure, "eastus",
            CloudProvider.Aws,   "unknown-region"));
    }

    [Fact]
    public void Roundtrip_AllCanonicals_StableAcrossProviders()
    {
        foreach (var canonical in RegionNormalizer.KnownCanonicalRegions)
        {
            foreach (var provider in new[] { CloudProvider.Azure, CloudProvider.Aws, CloudProvider.Gcp })
            {
                var slug = RegionNormalizer.ToProvider(provider, canonical);
                Assert.NotNull(slug);
                Assert.Equal(canonical, RegionNormalizer.ToCanonical(provider, slug!));
            }
        }
    }
}
