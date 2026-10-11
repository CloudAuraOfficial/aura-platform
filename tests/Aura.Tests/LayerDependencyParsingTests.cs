using System.Text.Json;
using Aura.Infrastructure.Services;
using Xunit;

namespace Aura.Tests;

// The one dependency rule, applied by the parser that run creation, validation and save all use: a name that matches
// no layer is rejected; a name that matches a disabled layer is dropped.
public class LayerDependencyParsingTests
{
    private static string Essence(string layers) => $$"""{ "layers": { {{layers}} } }""";

    [Fact]
    public void DependencyOnUnknownName_ThrowsAtRunCreation_WithTheUserMessage()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"gost\"] }");

        var ex = Assert.Throws<InvalidEssenceException>(() =>
            DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid()));

        Assert.Equal("Layer 'a' depends on 'gost', which does not exist.", ex.Message);
    }

    [Fact]
    public void DependencyOnDisabledLayer_IsDroppedAndTheLayerRuns()
    {
        var json = Essence(
            "\"a\": { \"isEnabled\": false, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [] }, " +
            "\"b\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"a\"] }");

        var layers = DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid());

        var only = Assert.Single(layers);
        Assert.Equal("b", only.LayerName);
        Assert.Empty(JsonSerializer.Deserialize<List<string>>(only.DependsOn)!);
    }

    [Fact]
    public void UnknownDependencyOnADisabledLayer_IsNotRead()
    {
        // Run creation skips a disabled layer entirely, so a typo in its dependsOn does not stop the essence.
        var json = Essence(
            "\"a\": { \"isEnabled\": false, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"gost\"] }, " +
            "\"b\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [] }");

        Assert.Single(DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid()));
    }

    [Fact]
    public void SelfDependency_IsReportedAsACycle()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"a\"] }");

        var ex = Assert.Throws<InvalidEssenceException>(() =>
            DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.NewGuid()));

        Assert.Equal("Cycle detected in layer dependencies.", ex.Message);
    }
}
