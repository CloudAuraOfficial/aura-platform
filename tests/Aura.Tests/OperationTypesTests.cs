using System.Text.RegularExpressions;
using Aura.Api.Services;
using Aura.Core.Enums;
using Aura.Core.Operations;
using Aura.Worker.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aura.Tests;

public class OperationTypesTests
{
    [Fact]
    public void All_has_no_duplicates_ignoring_case()
    {
        var distinct = OperationTypes.All.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Assert.Equal(OperationTypes.All.Count, distinct);
    }

    [Theory]
    [InlineData("CreateVM")]
    [InlineData("createvm")]
    [InlineData("HTTPHEALTHCHECK")]
    public void IsValid_accepts_registered_names_case_insensitively(string operationType)
    {
        Assert.True(OperationTypes.IsValid(operationType));
    }

    [Theory]
    [InlineData("DeployDeploymentManager")]
    [InlineData("")]
    [InlineData(" CreateVM")]
    [InlineData("CreateVM ")]
    [InlineData("CreateVm2")]
    public void IsValid_rejects_unknown_empty_and_untrimmed_values(string operationType)
    {
        Assert.False(OperationTypes.IsValid(operationType));
    }

    [Fact]
    public void IsValid_rejects_null()
    {
        Assert.False(OperationTypes.IsValid(null));
    }

    // The Worker must have a handler for exactly the shared list: a type with no handler
    // would pass generation and fail at run time, and a handler missing from the list would
    // be rejected by generation and save even though the Worker can run it.
    [Fact]
    public void Worker_registry_registers_exactly_the_shared_operation_types()
    {
        var registered = OperationRegistry.CreateDefault(new ServiceCollection()).RegisteredOperationTypes;

        var missingHandler = OperationTypes.All.Except(registered, StringComparer.OrdinalIgnoreCase).ToList();
        var missingFromList = registered.Except(OperationTypes.All, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Empty(missingHandler);
        Assert.Empty(missingFromList);
        Assert.Equal(OperationTypes.All.Count, registered.Count);
    }

    // The handler registration and the DI registration come from one call, so each registered type
    // must also be a transient service. A handler added to one but not the other would fail here.
    [Fact]
    public void Worker_di_registers_a_handler_for_every_operation_type()
    {
        var services = new ServiceCollection();
        OperationRegistry.CreateDefault(services);

        var handlerServices = services.Count(d => d.ServiceType.IsAssignableTo(typeof(Aura.Worker.Operations.IOperationHandler)));
        Assert.Equal(OperationTypes.All.Count, handlerServices);
    }

    [Fact]
    public void Canonicalize_returns_the_canonical_casing_or_null()
    {
        Assert.Equal("CreateVM", OperationTypes.Canonicalize("createvm"));
        Assert.Equal("HttpHealthCheck", OperationTypes.Canonicalize("HTTPHEALTHCHECK"));
        Assert.Null(OperationTypes.Canonicalize("Nope"));
        Assert.Null(OperationTypes.Canonicalize(null));
    }

    [Fact]
    public void Every_cloud_list_is_a_subset_of_All_and_together_they_cover_it()
    {
        var union = OperationTypes.ForCloud(CloudProvider.Azure)
            .Union(OperationTypes.ForCloud(CloudProvider.Aws))
            .Union(OperationTypes.ForCloud(CloudProvider.Gcp))
            .ToList();

        Assert.Empty(union.Except(OperationTypes.All));
        Assert.Empty(OperationTypes.All.Except(union));
    }

    [Fact]
    public void Cloud_lists_do_not_offer_another_clouds_handler_types()
    {
        Assert.DoesNotContain("CreateEc2Instance", OperationTypes.ForCloud(CloudProvider.Azure));
        Assert.DoesNotContain("CreateVM", OperationTypes.ForCloud(CloudProvider.Aws));
        Assert.DoesNotContain("CreateNetwork", OperationTypes.ForCloud(CloudProvider.Azure));
        Assert.Contains("HttpHealthCheck", OperationTypes.ForCloud(CloudProvider.Gcp));
    }

    [Fact]
    public void Emissionload_entrypoint_types_are_known_and_only_azure_runs_any()
    {
        Assert.Empty(OperationTypes.EmissionLoadEntrypoint(CloudProvider.Aws));
        Assert.Empty(OperationTypes.EmissionLoadEntrypoint(CloudProvider.Gcp));

        var azure = OperationTypes.EmissionLoadEntrypoint(CloudProvider.Azure);
        Assert.NotEmpty(azure);
        Assert.Empty(azure.Except(OperationTypes.ForCloud(CloudProvider.Azure)));
    }

    // The system prompts tell the model which types to use. Each type they name must be one the
    // platform accepts, or the model is steered toward a type that will be rejected or fail at run time.
    [Fact]
    public void Azure_system_prompt_names_only_valid_operation_types()
        => AssertPromptNamesValidTypes(AiEssenceBuilderService.AzureSystemPrompt);

    [Fact]
    public void Aws_system_prompt_names_only_valid_operation_types()
        => AssertPromptNamesValidTypes(AiEssenceBuilderService.AwsSystemPrompt);

    [Fact]
    public void Gcp_system_prompt_names_only_valid_operation_types()
        => AssertPromptNamesValidTypes(AiEssenceBuilderService.GcpSystemPrompt);

    private static void AssertPromptNamesValidTypes(string prompt)
    {
        var line = Regex.Match(prompt, @"operation types[^:]*:\s*([^\r\n]+)");
        Assert.True(line.Success, "Prompt lists no operation types.");

        var named = line.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(named);
        Assert.Empty(named.Where(t => !OperationTypes.IsValid(t)));
    }
}
