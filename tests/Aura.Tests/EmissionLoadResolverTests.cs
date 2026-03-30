using Aura.Core.Enums;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

public class EmissionLoadResolverTests
{
    [Theory]
    [InlineData("Azure", CloudProvider.Azure, "EmissionLoadACI")]
    [InlineData("Aws", CloudProvider.Aws, "EmissionLoadEC2")]
    [InlineData("Gcp", CloudProvider.Gcp, "EmissionLoadGCE")]
    [InlineData("azure", CloudProvider.Azure, "EmissionLoadVM")]
    public void ParseBaseEssence_ExtractsProviderAndBaseLoad(
        string cloudProviderStr, CloudProvider expectedProvider, string expectedBaseLoad)
    {
        var json = $$"""
        {
            "baseEssence": {
                "cloudProvider": "{{cloudProviderStr}}",
                "baseLoad": "{{expectedBaseLoad}}"
            },
            "layers": {}
        }
        """;

        var (provider, baseLoad) = EmissionLoadResolver.ParseBaseEssence(json);

        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedBaseLoad, baseLoad);
    }

    [Fact]
    public void ParseBaseEssence_MissingBaseEssence_ReturnsDefaults()
    {
        var json = """{ "layers": {} }""";

        var (provider, baseLoad) = EmissionLoadResolver.ParseBaseEssence(json);

        Assert.Equal(CloudProvider.Azure, provider);
        Assert.Equal("default", baseLoad);
    }

    [Fact]
    public void ParseBaseEssence_MissingFields_ReturnsDefaults()
    {
        var json = """{ "baseEssence": {} }""";

        var (provider, baseLoad) = EmissionLoadResolver.ParseBaseEssence(json);

        Assert.Equal(CloudProvider.Azure, provider);
        Assert.Equal("default", baseLoad);
    }

    [Fact]
    public void ParseBaseEssence_UnknownProvider_FallsBackToAzure()
    {
        var json = """
        {
            "baseEssence": {
                "cloudProvider": "oracle",
                "baseLoad": "EmissionLoadOCI"
            }
        }
        """;

        var (provider, baseLoad) = EmissionLoadResolver.ParseBaseEssence(json);

        Assert.Equal(CloudProvider.Azure, provider);
        Assert.Equal("EmissionLoadOCI", baseLoad);
    }

    [Fact]
    public void ParseBaseEssence_CaseInsensitiveProvider()
    {
        var json = """
        {
            "baseEssence": {
                "cloudProvider": "AWS",
                "baseLoad": "EmissionLoadS3"
            }
        }
        """;

        var (provider, _) = EmissionLoadResolver.ParseBaseEssence(json);

        Assert.Equal(CloudProvider.Aws, provider);
    }
}
