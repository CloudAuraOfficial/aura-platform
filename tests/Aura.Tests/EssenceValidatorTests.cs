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

        var check = Validator.CheckForSave(json);

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

        Assert.Null(Validator.CheckForSave(json).Error);
    }

    [Fact]
    public void Save_rejects_an_unknown_type_and_names_the_layer_and_type()
    {
        var json = Essence($"{KnownLayer}, {Layer("deploy-mgr", "DeployDeploymentManager")}");

        var reason = Validator.CheckForSave(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'deploy-mgr' uses 'DeployDeploymentManager'", reason);
        Assert.Contains($"Valid types: {string.Join(", ", OperationTypes.All)}.", reason);
    }

    [Fact]
    public void Save_rejects_unknown_type_on_a_disabled_layer()
    {
        // A disabled layer never runs today, but enabling it later would fail at run time.
        var json = Essence($"{KnownLayer}, {Layer("off", "DeployDeploymentManager", enabled: false)}");

        var reason = Validator.CheckForSave(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'off' uses 'DeployDeploymentManager'", reason);
    }

    [Fact]
    public void Save_stores_names_in_canonical_casing_including_the_parameters_fallback()
    {
        var json = Essence(
            "\"a\": { \"isEnabled\": true, \"operationType\": \"createvm\", \"parameters\": {}, \"dependsOn\": [] }, " +
            "\"b\": { \"isEnabled\": true, \"executorType\": \"operation\", \"parameters\": { \"operationType\": \"deletevm\" }, \"dependsOn\": [] }");

        var check = Validator.CheckForSave(json);

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

        Assert.Equal(json, Validator.CheckForSave(json).EssenceJson);
    }

    [Fact]
    public void Save_canonicalizes_an_emissionload_layer_so_the_case_sensitive_entrypoint_runs_it()
    {
        var check = Validator.CheckForSave(Essence("\"rg\": { \"isEnabled\": true, \"executorType\": \"emissionload\", \"operationType\": \"createresourcegroup\", \"parameters\": {}, \"dependsOn\": [] }"));

        Assert.Null(check.Error);
        Assert.Equal("CreateResourceGroup", StoredOperationType(check.EssenceJson, "rg"));
    }

    [Fact]
    public void Save_rejects_an_emissionload_layer_with_an_op_the_azure_entrypoint_does_not_run()
    {
        var json = Essence(Layer("net", "CreateVirtualNetwork", executorType: "emissionload"));

        var reason = Validator.CheckForSave(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'net' uses 'CreateVirtualNetwork'", reason);
    }

    [Fact]
    public void Save_accepts_a_powershell_layer_with_parameters_operation_type_and_leaves_it_unchanged()
    {
        var json = Essence("\"s\": { \"isEnabled\": true, \"executorType\": \"powershell\", \"scriptPath\": \"x.ps1\", \"parameters\": { \"operationType\": \"Anything at all\" }, \"dependsOn\": [] }");

        var check = Validator.CheckForSave(json);

        Assert.Null(check.Error);
        Assert.Equal(json, check.EssenceJson);
    }

    [Fact]
    public void Save_accepts_a_script_layer_with_an_unknown_type_in_parameters()
    {
        var json = Essence("\"s\": { \"isEnabled\": true, \"scriptPath\": \"x.ps1\", \"parameters\": { \"operationType\": \"Bogus\" }, \"dependsOn\": [] }");

        Assert.Null(Validator.CheckForSave(json).Error);
    }

    [Fact]
    public void Save_rejects_a_numeric_layer_operation_type_as_a_bad_request_reason()
    {
        var json = Essence("\"p\": { \"isEnabled\": true, \"operationType\": 42, \"parameters\": {}, \"dependsOn\": [] }");

        var reason = Validator.CheckForSave(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("Layer 'p': operationType must be a string.", reason);
    }

    [Fact]
    public void Save_rejects_a_numeric_parameters_operation_type_on_an_operation_layer()
    {
        var json = Essence("\"p\": { \"isEnabled\": true, \"executorType\": \"operation\", \"parameters\": { \"operationType\": 42 }, \"dependsOn\": [] }");

        var reason = Validator.CheckForSave(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("Layer 'p': parameters.operationType must be a string.", reason);
    }

    [Theory]
    [InlineData("\"operationType\": null")]
    [InlineData("\"operationType\": \"\"")]
    public void Save_treats_null_or_empty_type_as_absent(string operationTypeProperty)
    {
        var json = Essence($"\"p\": {{ \"isEnabled\": true, {operationTypeProperty}, \"parameters\": {{}}, \"dependsOn\": [] }}");

        Assert.Null(Validator.CheckForSave(json).Error);
    }

    [Fact]
    public void Save_passes_script_layers_without_a_type()
    {
        Assert.Null(Validator.CheckForSave(Essence("\"s\": { \"isEnabled\": true, \"scriptPath\": \"x.ps1\", \"dependsOn\": [] }")).Error);
    }

    [Fact]
    public void Save_rejects_an_essence_without_layers()
    {
        var reason = Validator.CheckForSave("{}").Error;

        Assert.Equal("JSON is valid but missing 'layers' property", reason);
    }

    [Theory]
    [InlineData("[]", "response is valid JSON but not an object")]
    [InlineData("\"just a string\"", "response is valid JSON but not an object")]
    [InlineData("null", "response is valid JSON but not an object")]
    [InlineData("{ \"layers\": [] }", "'layers' must be an object")]
    public void Save_rejects_json_that_is_not_an_essence_without_throwing(string json, string reason)
    {
        Assert.Equal(reason, Validator.CheckForSave(json).Error);
    }

    [Fact]
    public void Save_stores_renamed_essence_without_escaping_non_ascii_text()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"operationType\": \"createvm\", \"parameters\": { \"note\": \"ops — café\" }, \"dependsOn\": [] }");

        var stored = Validator.CheckForSave(json).EssenceJson;

        Assert.Contains("ops — café", stored);
        Assert.Equal("CreateVM", StoredOperationType(stored, "a"));
    }

    [Fact]
    public void Save_rejects_input_that_is_not_json()
    {
        Assert.Equal("Essence JSON is not valid.", Validator.CheckForSave("not json").Error);
    }

    // ---- save: the run creation parser runs first, so what save rejects is what run creation would reject ----

    [Fact]
    public void Save_rejects_a_dependency_cycle_and_names_the_problem()
    {
        var json = Essence(
            "\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"b\"] }, " +
            "\"b\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"a\"] }");

        Assert.Equal(new[] { "Cycle detected in layer dependencies." }, Validator.CheckForSave(json).Errors);
    }

    [Fact]
    public void Save_rejects_an_essence_whose_layers_are_all_disabled()
    {
        var json = Essence(Layer("off", "CreateResourceGroup", enabled: false));

        Assert.Equal(new[] { "essence has no enabled layers" }, Validator.CheckForSave(json).Errors);
    }

    [Fact]
    public void Save_rejects_an_empty_layers_object()
    {
        Assert.Equal(new[] { "essence has no enabled layers" }, Validator.CheckForSave(Essence("")).Errors);
    }

    [Fact]
    public void Save_rejects_an_unknown_run_policy_and_names_the_layer()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"runPolicy\": \"sometimes\", \"parameters\": {}, \"dependsOn\": [] }");

        var reason = Validator.CheckForSave(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("unknown runPolicy", reason);
    }

    [Fact]
    public void Save_rejects_a_dependency_on_a_missing_layer()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"ghost\"] }");

        var reason = Validator.CheckForSave(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("Layer 'a' depends on 'ghost'", reason);
    }

    [Fact]
    public void Save_reports_every_structural_problem_not_just_the_first()
    {
        var json = Essence(
            "\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"b\"] }, " +
            "\"b\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"a\"] }, " +
            "\"c\": { \"isEnabled\": true, \"operationType\": \"DeployDeploymentManager\", \"parameters\": {}, \"dependsOn\": [] }");

        var errors = Validator.CheckForSave(json).Errors;

        Assert.Equal(2, errors.Count);
        Assert.Contains("Cycle detected in layer dependencies.", errors);
        Assert.Contains(errors, e => e.Contains("layer 'c' uses 'DeployDeploymentManager'"));
    }

    [Fact]
    public void Save_rejects_a_non_boolean_is_enabled()
    {
        var json = Essence("\"a\": { \"isEnabled\": \"yes\", \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [] }");

        Assert.Equal(new[] { "Layer 'a': isEnabled must be true or false." }, Validator.CheckForSave(json).Errors);
    }

    [Fact]
    public void Save_accepts_a_valid_essence_and_stores_it_unchanged_when_no_rename_is_needed()
    {
        var json = Essence($"{KnownLayer}, {Layer("ec2", "CreateEc2Instance")}");

        var check = Validator.CheckForSave(json);

        Assert.Empty(check.Errors);
        Assert.Equal(json, check.EssenceJson);
    }

    // ---- deployment validation: the parser run creation uses, then the operation types on any cloud ----

    private static EssenceCheck ValidateForDeployment(string json) => Validator.Validate(json, cloud: null, canonicalize: false);

    [Fact]
    public void Deployment_accepts_an_essence_run_creation_can_use()
    {
        Assert.Null(ValidateForDeployment(Essence(KnownLayer)).Error);
    }

    [Fact]
    public void Deployment_accepts_a_mixed_cloud_essence_because_save_does()
    {
        // Save accepts layers from several clouds, so validation must not reject what save stored.
        var json = Essence($"{KnownLayer}, {Layer("ec2", "CreateEc2Instance")}");

        Assert.Null(ValidateForDeployment(json).Error);
        Assert.Null(Validator.CheckForSave(json).Error);
    }

    [Fact]
    public void Deployment_reports_a_dependency_cycle_the_parser_rejects()
    {
        var json = Essence(
            "\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"b\"] }, " +
            "\"b\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"a\"] }");

        Assert.Equal(new[] { "Cycle detected in layer dependencies." }, ValidateForDeployment(json).Errors);
    }

    [Fact]
    public void Deployment_reports_an_unknown_run_policy_the_parser_rejects()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"runPolicy\": \"sometimes\", \"parameters\": {}, \"dependsOn\": [] }");

        Assert.Contains("unknown runPolicy", ValidateForDeployment(json).Error);
    }

    [Fact]
    public void Deployment_reports_an_unknown_executor_type_with_the_parser_message()
    {
        var json = Essence("\"a\": { \"isEnabled\": true, \"executorType\": \"cobol\", \"scriptPath\": \"x\", \"parameters\": {}, \"dependsOn\": [] }");

        Assert.Equal(new[] { "Unknown executor type: cobol" }, ValidateForDeployment(json).Errors);
    }

    [Fact]
    public void Deployment_reports_an_unknown_operation_type_and_names_the_layer()
    {
        var reason = ValidateForDeployment(Essence(Layer("x", "Nope"))).Error;

        Assert.NotNull(reason);
        Assert.Contains("Unknown operationType", reason);
        Assert.Contains("layer 'x' uses 'Nope'", reason);
    }

    [Fact]
    public void Deployment_reports_an_emissionload_type_its_container_cannot_run()
    {
        var json = Essence(Layer("net", "CreateVirtualNetwork", executorType: "emissionload"));

        var reason = ValidateForDeployment(json).Error;

        Assert.NotNull(reason);
        Assert.Contains("layer 'net' uses 'CreateVirtualNetwork'", reason);
    }

    [Fact]
    public void Deployment_reports_every_problem_when_a_cycle_and_an_unknown_type_are_both_present()
    {
        var json = Essence(
            "\"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"b\"] }, " +
            "\"b\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": {}, \"dependsOn\": [\"a\"] }, " +
            Layer("c", "Nope"));

        var errors = ValidateForDeployment(json).Errors;

        Assert.Equal(2, errors.Count);
        Assert.Equal("Cycle detected in layer dependencies.", errors[0]);
        Assert.Contains("layer 'c' uses 'Nope'", errors[1]);
    }

    [Fact]
    public void Deployment_rejects_an_essence_with_no_enabled_layers_including_an_empty_layer_set()
    {
        Assert.Equal("essence has no enabled layers", ValidateForDeployment(Essence(Layer("off", "CreateResourceGroup", enabled: false))).Error);
        Assert.Equal("essence has no enabled layers", ValidateForDeployment("{ \"layers\": {} }").Error);
    }

    [Fact]
    public void Deployment_rejects_an_empty_object_as_missing_layers()
    {
        Assert.Equal("JSON is valid but missing 'layers' property", ValidateForDeployment("{}").Error);
    }

    [Fact]
    public void Deployment_does_not_check_operation_types_on_disabled_layers_that_run_creation_skips()
    {
        // A disabled legacy layer with an unknown type is runnable, so it must not be reported invalid.
        var json = Essence($"{KnownLayer}, {Layer("legacy", "RetiredOperation", enabled: false)}");

        Assert.Null(ValidateForDeployment(json).Error);
    }

    [Fact]
    public void Deployment_returns_the_canonical_casing_when_asked_to_canonicalize()
    {
        var check = Validator.Validate(Essence(Layer("rg", "createresourcegroup")), cloud: null);

        Assert.Null(check.Error);
        Assert.Equal("CreateResourceGroup", StoredOperationType(check.EssenceJson, "rg"));
    }

    [Fact]
    public void Deployment_skips_the_rewrite_and_returns_the_input_unchanged_when_not_asked()
    {
        var json = Essence(Layer("rg", "createresourcegroup"));

        var check = ValidateForDeployment(json);

        Assert.Null(check.Error);
        Assert.Equal(json, check.EssenceJson);
    }

    [Fact]
    public void Deployment_reports_malformed_json_without_parser_internals()
    {
        Assert.Equal("Essence JSON is not valid.", ValidateForDeployment("{ not json").Error);
    }

    [Fact]
    public void Deployment_rejects_a_non_object_root_with_the_exact_message()
    {
        Assert.Equal("response is valid JSON but not an object", ValidateForDeployment("[]").Error);
    }

    [Theory]
    [InlineData("\"layers\": []", "'layers' must be an object")]
    [InlineData("\"layers\": { \"a\": 5 }", "Layer 'a' must be an object.")]
    [InlineData("\"layers\": { \"a\": { \"isEnabled\": \"yes\" } }", "Layer 'a': isEnabled must be true or false.")]
    [InlineData("\"layers\": { \"a\": { \"isEnabled\": null } }", "Layer 'a': isEnabled must be true or false.")]
    [InlineData("\"layers\": { \"a\": { \"isEnabled\": true, \"executorType\": 3, \"parameters\": {} } }", "Layer 'a': executorType must be a string.")]
    [InlineData("\"layers\": { \"a\": { \"isEnabled\": true, \"scriptPath\": 3, \"parameters\": {} } }", "Layer 'a': scriptPath must be a string.")]
    [InlineData("\"layers\": { \"a\": { \"isEnabled\": true, \"dependsOn\": [1], \"scriptPath\": \"x\", \"parameters\": {} } }", "Layer 'a': dependsOn must list layer names.")]
    [InlineData("\"layers\": { \"a\": { \"isEnabled\": true, \"operationType\": \"CreateResourceGroup\", \"parameters\": [] } }", "Layer 'a': parameters must be an object when operationType is set.")]
    public void Deployment_names_a_malformed_shape_with_a_curated_message(string layers, string expected)
    {
        var json = $"{{ {AzureBase}, {layers} }}";

        Assert.Equal(new[] { expected }, ValidateForDeployment(json).Errors);
    }

    [Fact]
    public void Deployment_lets_an_unexpected_parser_exception_propagate()
    {
        // Only InvalidEssenceException is a reason for the user. Anything else is a bug and must not become a verdict.
        Assert.Throws<NotSupportedException>(() => Validator.Validate(
            Essence(KnownLayer), null, canonicalize: false,
            _ => throw new NotSupportedException("boom")));
    }
}
