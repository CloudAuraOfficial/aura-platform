using Aura.Api.Services;
using Aura.Core.Operations;
using Xunit;

namespace Aura.Tests;

public class EssenceValidatorTests
{
    private static readonly EssenceValidator Validator = new();

    private static string Essence(string layers) => $$"""
        {
          "baseEssence": { "cloudProvider": "Azure", "defaultRegion": "eastus", "baseLoad": "EmissionLoadVM", "uniqueId": "t" },
          "layers": { {{layers}} }
        }
        """;

    private const string KnownLayer =
        "\"create-rg\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": { \"resourceGroup\": \"rg\" }, \"dependsOn\": [] }";

    private static string UnknownLayer(string name, string type, bool enabled = true) =>
        $"\"{name}\": {{ \"isEnabled\": {(enabled ? "true" : "false")}, \"operationType\": \"{type}\", \"parameters\": {{}}, \"dependsOn\": [] }}";

    [Fact]
    public void Validate_accepts_known_types_on_enabled_layers()
    {
        Assert.Null(Validator.Validate(Essence(KnownLayer)));
    }

    [Fact]
    public void Validate_accepts_known_type_in_any_letter_case()
    {
        var json = Essence("\"create-rg\": { \"isEnabled\": true, \"operationType\": \"createresourcegroup\", \"parameters\": {}, \"dependsOn\": [] }");

        Assert.Null(Validator.Validate(json));
    }

    [Fact]
    public void Validate_rejects_unknown_type_and_names_the_layer_and_type()
    {
        var json = Essence($"{KnownLayer}, {UnknownLayer("deploy-mgr", "DeployDeploymentManager")}");

        var reason = Validator.Validate(json);

        Assert.NotNull(reason);
        Assert.Contains("layer 'deploy-mgr' uses 'DeployDeploymentManager'", reason);
    }

    [Fact]
    public void Validate_reason_lists_the_valid_types_for_the_model_to_choose_from()
    {
        var json = Essence($"{KnownLayer}, {UnknownLayer("x", "Nope")}");

        var reason = Validator.Validate(json);

        Assert.NotNull(reason);
        Assert.Contains($"Valid types: {string.Join(", ", OperationTypes.All)}.", reason);
    }

    [Fact]
    public void Validate_reports_every_unknown_type()
    {
        var json = Essence($"{UnknownLayer("a", "Nope1")}, {UnknownLayer("b", "Nope2")}");

        var reason = Validator.Validate(json);

        Assert.NotNull(reason);
        Assert.Contains("layer 'a' uses 'Nope1'", reason);
        Assert.Contains("layer 'b' uses 'Nope2'", reason);
    }

    [Fact]
    public void Validate_still_rejects_essence_without_enabled_layers()
    {
        var json = Essence(UnknownLayer("off", "Nope", enabled: false));

        // The parser finds no enabled layers, so the earlier check wins over the operation-type check.
        Assert.Equal("essence has no enabled layers", Validator.Validate(json));
    }

    [Fact]
    public void Validate_keeps_structural_checks_before_operation_types()
    {
        Assert.Equal("JSON is valid but missing 'layers' property", Validator.Validate("{}"));
        Assert.Equal("response is valid JSON but not an object", Validator.Validate("[]"));
    }

    [Fact]
    public void Validate_returns_parser_reason_for_non_json()
    {
        Assert.NotNull(Validator.Validate("not json"));
    }

    [Fact]
    public void ValidateOperationTypes_rejects_unknown_type_on_a_disabled_layer()
    {
        // A disabled layer never runs today, but enabling it later would fail at run time.
        var json = Essence($"{KnownLayer}, {UnknownLayer("off", "DeployDeploymentManager", enabled: false)}");

        var reason = Validator.ValidateOperationTypes(json);

        Assert.NotNull(reason);
        Assert.Contains("layer 'off' uses 'DeployDeploymentManager'", reason);
    }

    [Fact]
    public void ValidateOperationTypes_rejects_unknown_fallback_type_in_parameters()
    {
        // The Worker falls back to parameters.operationType when the layer-level value is absent.
        var json = Essence("\"p\": { \"isEnabled\": true, \"executorType\": \"operation\", \"parameters\": { \"operationType\": \"Bogus\" }, \"dependsOn\": [] }");

        var reason = Validator.ValidateOperationTypes(json);

        Assert.NotNull(reason);
        Assert.Contains("layer 'p' uses 'Bogus'", reason);
    }

    [Fact]
    public void ValidateOperationTypes_ignores_parameters_type_when_layer_type_is_set()
    {
        // The layer-level type is the one that runs, so an unrelated value in parameters is not checked.
        var json = Essence("\"p\": { \"isEnabled\": true, \"operationType\": \"CreateVM\", \"parameters\": { \"operationType\": \"Bogus\" }, \"dependsOn\": [] }");

        Assert.Null(Validator.ValidateOperationTypes(json));
    }

    [Theory]
    [InlineData("\"operationType\": 42")]
    [InlineData("\"operationType\": null")]
    [InlineData("\"operationType\": \"\"")]
    public void ValidateOperationTypes_treats_non_string_or_empty_type_as_absent(string operationTypeProperty)
    {
        var json = Essence($"\"p\": {{ \"isEnabled\": true, {operationTypeProperty}, \"parameters\": {{}}, \"dependsOn\": [] }}");

        Assert.Null(Validator.ValidateOperationTypes(json));
    }

    [Fact]
    public void ValidateOperationTypes_passes_layers_without_a_type_and_essences_without_layers()
    {
        Assert.Null(Validator.ValidateOperationTypes(Essence("\"s\": { \"isEnabled\": true, \"scriptPath\": \"x.ps1\", \"dependsOn\": [] }")));
        Assert.Null(Validator.ValidateOperationTypes("{}"));
    }

    [Fact]
    public void ValidateOperationTypes_passes_input_that_is_not_json()
    {
        // Save does not run the full check; structure is validated by the generation path, not here.
        Assert.Null(Validator.ValidateOperationTypes("not json"));
    }
}
