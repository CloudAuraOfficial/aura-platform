using System.Text.Json;
using System.Text.RegularExpressions;
using Aura.Api.Services;
using Aura.Core.Enums;
using Aura.Core.Operations;
using Xunit;

namespace Aura.Tests;

public class EssenceValidatorTests
{
    private static readonly EssenceValidator Validator = new();

    private const string AzureBase = "\"baseEssence\": { \"cloudProvider\": \"Azure\", \"defaultRegion\": \"eastus\", \"baseLoad\": \"EmissionLoadVM\", \"uniqueId\": \"t\" }";

    private static string Essence(string layers, string baseEssence = AzureBase) => $$"""
        {
          {{baseEssence}},
          "layers": { {{layers}} }
        }
        """;

    private const string KnownLayer =
        "\"create-rg\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": { \"resourceGroup\": \"rg\" }, \"dependsOn\": [] }";

    private static string Layer(string name, string type, bool enabled = true, string executorType = "operation") =>
        $"\"{name}\": {{ \"isEnabled\": {(enabled ? "true" : "false")}, \"executorType\": \"{executorType}\", \"operationType\": \"{type}\", \"parameters\": {{}}, \"dependsOn\": [] }}";

    // The layer's stored operationType, read back from the JSON the validator returned.
    private static string? StoredOperationType(string json, string layer, string? property = null)
    {
        using var doc = JsonDocument.Parse(json);
        var element = doc.RootElement.GetProperty("layers").GetProperty(layer);
        if (property is not null)
            element = element.GetProperty(property);
        return element.TryGetProperty("operationType", out var op) ? op.GetString() : null;
    }

    private static string? StoredParameterOperationType(string json, string layer)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("layers").GetProperty(layer).GetProperty("parameters")
            .GetProperty("operationType").GetString();
    }

    // ---- generation: checked against the requested cloud ----

    [Fact]
    public void Validate_accepts_known_types_for_the_requested_cloud()
    {
        Assert.Null(Validator.Validate(Essence(KnownLayer), CloudProvider.Azure).Error);
    }

    [Fact]
    public void Validate_rejects_an_aws_type_for_an_azure_essence_and_names_the_layer()
    {
        var reason = Validator.Validate(Essence($"{KnownLayer}, {Layer("ec2", "CreateEc2Instance")}"), CloudProvider.Azure).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'ec2' uses 'CreateEc2Instance'", reason);
        Assert.Contains("Not an Azure operation type", reason);
    }

    [Fact]
    public void Validate_retry_feedback_lists_only_the_requested_clouds_types()
    {
        var reason = Validator.Validate(Essence(Layer("ec2", "CreateEc2Instance")), CloudProvider.Azure).Error;

        Assert.NotNull(reason);
        // The feedback is what steers the retry, so it must not offer types from other clouds.
        var listed = Regex.Match(reason, @"Valid Azure types: ([^.]+)\.").Groups[1].Value
            .Split(", ", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(OperationTypes.ForCloud(CloudProvider.Azure), listed);
        Assert.Empty(listed.Except(OperationTypes.ForCloud(CloudProvider.Azure)));
    }

    [Fact]
    public void Validate_unknown_type_feedback_lists_the_requested_clouds_types()
    {
        var reason = Validator.Validate(Essence(Layer("x", "Nope")), CloudProvider.Aws).Error;

        Assert.NotNull(reason);
        Assert.Contains("Unknown operationType: layer 'x' uses 'Nope'", reason);
        Assert.Contains($"Valid types: {string.Join(", ", OperationTypes.ForCloud(CloudProvider.Aws))}.", reason);
    }

    [Fact]
    public void Validate_reports_every_out_of_scope_and_unknown_layer_together()
    {
        var json = Essence($"{Layer("a", "Nope1")}, {Layer("b", "CreateEc2Instance")}");

        var reason = Validator.Validate(json, CloudProvider.Azure).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'a' uses 'Nope1'", reason);
        Assert.Contains("layer 'b' uses 'CreateEc2Instance'", reason);
    }

    [Theory]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Gcp)]
    public void Validate_accepts_the_cloud_agnostic_health_check_on_every_cloud(CloudProvider cloud)
    {
        var json = Essence(Layer("probe", "HttpHealthCheck"), cloud == CloudProvider.Aws
            ? "\"baseEssence\": { \"cloudProvider\": \"Aws\" }"
            : "\"baseEssence\": { \"cloudProvider\": \"Gcp\" }");

        Assert.Null(Validator.Validate(json, cloud).Error);
    }

    [Fact]
    public void Validate_accepts_known_type_in_any_letter_case_and_returns_canonical_casing()
    {
        var check = Validator.Validate(Essence("\"create-rg\": { \"isEnabled\": true, \"operationType\": \"createresourcegroup\", \"parameters\": {}, \"dependsOn\": [] }"), CloudProvider.Azure);

        Assert.Null(check.Error);
        Assert.Equal("CreateResourceGroup", StoredOperationType(check.EssenceJson, "create-rg"));
    }

    [Fact]
    public void Validate_still_rejects_essence_without_enabled_layers()
    {
        var json = Essence(Layer("off", "Nope", enabled: false));

        // The parser finds no enabled layers, so the earlier check wins over the operation-type check.
        Assert.Equal("essence has no enabled layers", Validator.Validate(json, CloudProvider.Azure).Error);
    }

    [Fact]
    public void Validate_keeps_structural_checks_before_operation_types()
    {
        Assert.Equal("JSON is valid but missing 'layers' property", Validator.Validate("{}", CloudProvider.Azure).Error);
        Assert.Equal("response is valid JSON but not an object", Validator.Validate("[]", CloudProvider.Azure).Error);
    }

    [Fact]
    public void Validate_returns_parser_reason_for_non_json()
    {
        Assert.NotNull(Validator.Validate("not json", CloudProvider.Azure).Error);
    }

    [Fact]
    public void Validate_rejects_a_numeric_layer_operation_type_with_the_layer_named()
    {
        var json = Essence("\"p\": { \"isEnabled\": true, \"operationType\": 42, \"parameters\": {}, \"dependsOn\": [] }");

        var reason = Validator.Validate(json, CloudProvider.Azure).Error;

        Assert.NotNull(reason);
        Assert.Contains("Layer 'p': operationType must be a string.", reason);
    }

    [Fact]
    public void Validate_accepts_a_powershell_layer_whose_parameters_carry_an_operation_type_argument()
    {
        var json = Essence("\"s\": { \"isEnabled\": true, \"executorType\": \"powershell\", \"scriptPath\": \"x.ps1\", \"parameters\": { \"operationType\": 7 }, \"dependsOn\": [] }");

        Assert.Null(Validator.Validate(json, CloudProvider.Azure).Error);
    }

    // ---- emissionload: checked against what the container's entrypoint runs ----

    [Fact]
    public void Validate_accepts_an_emissionload_layer_the_azure_entrypoint_runs()
    {
        var json = Essence(Layer("rg", "CreateResourceGroup", executorType: "emissionload"));

        Assert.Null(Validator.Validate(json, CloudProvider.Azure).Error);
    }

    [Fact]
    public void Validate_rejects_an_emissionload_layer_with_an_op_the_entrypoint_does_not_run()
    {
        // CreateVirtualNetwork is an Azure handler, but the Azure EmissionLoad entrypoint has no case for it.
        var json = Essence(Layer("net", "CreateVirtualNetwork", executorType: "emissionload"));

        var reason = Validator.Validate(json, CloudProvider.Azure).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'net' uses 'CreateVirtualNetwork'", reason);
        Assert.Contains("EmissionLoad Azure container cannot run", reason);
    }

    [Fact]
    public void Validate_rejects_an_emissionload_layer_on_aws_since_that_entrypoint_runs_no_operation()
    {
        var json = Essence(Layer("vpc", "CreateVpc", executorType: "emissionload"),
            "\"baseEssence\": { \"cloudProvider\": \"Aws\" }");

        var reason = Validator.Validate(json, CloudProvider.Aws).Error;

        Assert.NotNull(reason);
        Assert.Contains("EmissionLoad Aws container cannot run", reason);
        Assert.Contains("It runs: none.", reason);
    }

    // ---- save, update and clone: known on any cloud, canonical names, cross-cloud essences allowed ----

    [Fact]
    public void Save_accepts_the_multicloud_essence_with_no_declared_cloud_and_an_azure_account()
    {
        // The demo's multi-cloud showpiece: no baseEssence.cloudProvider, Azure, AWS and GCP layers.
        var json = """
            {
              "layers": {
                "azure-rg": { "isEnabled": true, "operationType": "CreateResourceGroup", "parameters": { "resourceGroupName": "rg" }, "dependsOn": [] },
                "aws-vpc": { "isEnabled": true, "operationType": "CreateVpc", "parameters": { "cidr": "10.0.0.0/16" }, "dependsOn": ["azure-rg"] },
                "gcp-net": { "isEnabled": true, "operationType": "CreateNetwork", "parameters": {}, "dependsOn": ["azure-rg"] }
              }
            }
            """;

        var check = Validator.CheckOperationTypes(json);

        Assert.Null(check.Error);
        Assert.Equal(json, check.EssenceJson);
    }

    [Fact]
    public void Save_accepts_an_aws_essence_that_deletes_gcp_and_azure_resources()
    {
        // The "ops — multicloud full cleanup" shape: an AWS account with GCP and Azure delete layers.
        var json = Essence(
            "\"gcs\": { \"isEnabled\": true, \"operationType\": \"DeleteGcsBucket\", \"parameters\": {}, \"dependsOn\": [] }, " +
            "\"rg\": { \"isEnabled\": true, \"operationType\": \"DeleteResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"gcs\"] }",
            "\"baseEssence\": { \"cloudProvider\": \"Aws\", \"defaultRegion\": \"us-east-1\" }");

        Assert.Null(Validator.CheckOperationTypes(json).Error);
    }

    [Fact]
    public void Save_rejects_an_unknown_type_and_names_the_layer_and_type()
    {
        var json = Essence($"{KnownLayer}, {Layer("deploy-mgr", "DeployDeploymentManager")}");

        var reason = Validator.CheckOperationTypes(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'deploy-mgr' uses 'DeployDeploymentManager'", reason);
        Assert.Contains($"Valid types: {string.Join(", ", OperationTypes.All)}.", reason);
    }

    [Fact]
    public void Save_rejects_unknown_type_on_a_disabled_layer()
    {
        // A disabled layer never runs today, but enabling it later would fail at run time.
        var json = Essence($"{KnownLayer}, {Layer("off", "DeployDeploymentManager", enabled: false)}");

        var reason = Validator.CheckOperationTypes(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'off' uses 'DeployDeploymentManager'", reason);
    }

    [Fact]
    public void Save_stores_names_in_canonical_casing_including_the_parameters_fallback()
    {
        var json = Essence(
            "\"a\": { \"isEnabled\": true, \"operationType\": \"createvm\", \"parameters\": {}, \"dependsOn\": [] }, " +
            "\"b\": { \"isEnabled\": true, \"executorType\": \"operation\", \"parameters\": { \"operationType\": \"deletevm\" }, \"dependsOn\": [] }");

        var check = Validator.CheckOperationTypes(json);

        Assert.Null(check.Error);
        Assert.Equal("CreateVM", StoredOperationType(check.EssenceJson, "a"));
        Assert.Equal("DeleteVM", StoredParameterOperationType(check.EssenceJson, "b"));
        Assert.DoesNotContain("createvm", check.EssenceJson);
        Assert.DoesNotContain("deletevm", check.EssenceJson);
    }

    [Fact]
    public void Save_stores_an_essence_already_in_canonical_casing_exactly_as_sent()
    {
        var json = Essence(KnownLayer);

        Assert.Equal(json, Validator.CheckOperationTypes(json).EssenceJson);
    }

    [Fact]
    public void Save_canonicalizes_an_emissionload_layer_so_the_case_sensitive_entrypoint_runs_it()
    {
        var check = Validator.CheckOperationTypes(Essence("\"rg\": { \"isEnabled\": true, \"executorType\": \"emissionload\", \"operationType\": \"createresourcegroup\", \"parameters\": {}, \"dependsOn\": [] }"));

        Assert.Null(check.Error);
        Assert.Equal("CreateResourceGroup", StoredOperationType(check.EssenceJson, "rg"));
    }

    [Fact]
    public void Save_rejects_an_emissionload_layer_with_an_op_the_azure_entrypoint_does_not_run()
    {
        var json = Essence(Layer("net", "CreateVirtualNetwork", executorType: "emissionload"));

        var reason = Validator.CheckOperationTypes(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'net' uses 'CreateVirtualNetwork'", reason);
    }

    [Fact]
    public void Save_accepts_a_powershell_layer_with_parameters_operation_type_and_leaves_it_unchanged()
    {
        var json = Essence("\"s\": { \"isEnabled\": true, \"executorType\": \"powershell\", \"scriptPath\": \"x.ps1\", \"parameters\": { \"operationType\": \"Anything at all\" }, \"dependsOn\": [] }");

        var check = Validator.CheckOperationTypes(json);

        Assert.Null(check.Error);
        Assert.Equal(json, check.EssenceJson);
    }

    [Fact]
    public void Save_accepts_a_script_layer_with_an_unknown_type_in_parameters()
    {
        var json = Essence("\"s\": { \"isEnabled\": true, \"scriptPath\": \"x.ps1\", \"parameters\": { \"operationType\": \"Bogus\" }, \"dependsOn\": [] }");

        Assert.Null(Validator.CheckOperationTypes(json).Error);
    }

    [Fact]
    public void Save_rejects_a_numeric_layer_operation_type_as_a_bad_request_reason()
    {
        var json = Essence("\"p\": { \"isEnabled\": true, \"operationType\": 42, \"parameters\": {}, \"dependsOn\": [] }");

        var reason = Validator.CheckOperationTypes(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("Layer 'p': operationType must be a string.", reason);
    }

    [Fact]
    public void Save_rejects_a_numeric_parameters_operation_type_on_an_operation_layer()
    {
        var json = Essence("\"p\": { \"isEnabled\": true, \"executorType\": \"operation\", \"parameters\": { \"operationType\": 42 }, \"dependsOn\": [] }");

        var reason = Validator.CheckOperationTypes(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("Layer 'p': parameters.operationType must be a string.", reason);
    }

    [Theory]
    [InlineData("\"operationType\": null")]
    [InlineData("\"operationType\": \"\"")]
    public void Save_treats_null_or_empty_type_as_absent(string operationTypeProperty)
    {
        var json = Essence($"\"p\": {{ \"isEnabled\": true, {operationTypeProperty}, \"parameters\": {{}}, \"dependsOn\": [] }}");

        Assert.Null(Validator.CheckOperationTypes(json).Error);
    }

    [Fact]
    public void Save_passes_layers_without_a_type_and_essences_without_layers()
    {
        Assert.Null(Validator.CheckOperationTypes(Essence("\"s\": { \"isEnabled\": true, \"scriptPath\": \"x.ps1\", \"dependsOn\": [] }")).Error);
        Assert.Null(Validator.CheckOperationTypes("{}").Error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("null")]
    public void Save_passes_valid_json_that_is_not_an_object_without_throwing(string json)
    {
        Assert.Null(Validator.CheckOperationTypes(json).Error);
    }

    [Fact]
    public void Save_stores_renamed_essence_without_escaping_non_ascii_text()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"operationType\": \"createvm\", \"parameters\": { \"note\": \"ops — café\" }, \"dependsOn\": [] }");

        var stored = Validator.CheckOperationTypes(json).EssenceJson;

        Assert.Contains("ops — café", stored);
        Assert.Equal("CreateVM", StoredOperationType(stored, "a"));
    }

    [Fact]
    public void Save_passes_input_that_is_not_json()
    {
        // Save does not run the full check; structure is validated by the generation path, not here.
        var check = Validator.CheckOperationTypes("not json");

        Assert.Null(check.Error);
        Assert.Equal("not json", check.EssenceJson);
    }

    // ---- deployment validation: the parser run creation uses, plus the save path's operation-type checks ----

    [Fact]
    public void CheckRunnable_accepts_an_essence_run_creation_can_use()
    {
        Assert.Null(Validator.CheckRunnable(Essence(KnownLayer)).Error);
    }

    [Fact]
    public void CheckRunnable_accepts_a_mixed_cloud_essence_because_save_does()
    {
        // Save accepts layers from several clouds, so validation must not reject what save stored.
        var json = Essence($"{KnownLayer}, {Layer("ec2", "CreateEc2Instance")}");

        Assert.Null(Validator.CheckRunnable(json).Error);
        Assert.Null(Validator.CheckOperationTypes(json).Error);
    }

    [Fact]
    public void CheckRunnable_reports_a_dependency_cycle_the_parser_rejects()
    {
        var json = Essence(
            "\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"b\"] }, " +
            "\"b\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"a\"] }");

        var reason = Validator.CheckRunnable(json).Error;

        Assert.Equal("Cycle detected in layer dependencies.", reason);
    }

    [Fact]
    public void CheckRunnable_reports_an_unknown_run_policy_the_parser_rejects()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"runPolicy\": \"sometimes\", \"parameters\": {}, \"dependsOn\": [] }");

        Assert.Contains("unknown runPolicy", Validator.CheckRunnable(json).Error);
    }

    [Fact]
    public void CheckRunnable_reports_an_unknown_operation_type_and_names_the_layer()
    {
        var reason = Validator.CheckRunnable(Essence(Layer("x", "Nope"))).Error;

        Assert.NotNull(reason);
        Assert.Contains("Unknown operationType", reason);
        Assert.Contains("layer 'x' uses 'Nope'", reason);
    }

    [Fact]
    public void CheckRunnable_reports_an_emissionload_type_its_container_cannot_run()
    {
        var json = Essence(Layer("net", "CreateVirtualNetwork", executorType: "emissionload"));

        var reason = Validator.CheckRunnable(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'net' uses 'CreateVirtualNetwork'", reason);
    }

    [Fact]
    public void CheckRunnable_reports_malformed_json_without_parser_internals()
    {
        Assert.Equal("Essence JSON is not valid.", Validator.CheckRunnable("{ not json").Error);
    }

    [Fact]
    public void CheckRunnable_rejects_a_non_object_root()
    {
        Assert.NotNull(Validator.CheckRunnable("[]").Error);
    }

    [Fact]
    public void CheckRunnable_returns_the_canonical_casing_the_save_path_stores()
    {
        var check = Validator.CheckRunnable(Essence(Layer("rg", "createresourcegroup")));

        Assert.Null(check.Error);
        Assert.Equal("CreateResourceGroup", StoredOperationType(check.EssenceJson, "rg"));
    }
}
