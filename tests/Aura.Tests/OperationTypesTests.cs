using System.Text.RegularExpressions;
using Aura.Api.Services;
using Aura.Core.Operations;
using Aura.Worker.Operations;
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
        var registered = OperationRegistry.CreateDefault().RegisteredOperationTypes;

        var missingHandler = OperationTypes.All.Except(registered, StringComparer.OrdinalIgnoreCase).ToList();
        var missingFromList = registered.Except(OperationTypes.All, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Empty(missingHandler);
        Assert.Empty(missingFromList);
        Assert.Equal(OperationTypes.All.Count, registered.Count);
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
