using System.Diagnostics;
using System.Text.Json;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Services;

public class AiEssenceBuilderService
{
    private readonly ILlmProviderFactory _providerFactory;
    private readonly UserAiKeyService _keyService;
    private readonly AuraDbContext _db;
    private readonly ILogger<AiEssenceBuilderService> _logger;

    private const int MaxIterations = 3;

    private const string AzureSystemPrompt = """
        You are an infrastructure-as-code expert for the Aura Platform. Your job is to generate a valid Aura Essence JSON definition based on the user's natural language description.

        An Essence JSON has this structure:
        {
          "baseEssence": {
            "cloudProvider": "Azure" | "Aws" | "Gcp",
            "defaultRegion": "<region>",
            "baseLoad": "<execution strategy, e.g. EmissionLoadVM, EmissionLoadACI>",
            "uniqueId": "<unique identifier>",
            "subscriptionId": "<optional subscription/account ID>"
          },
          "layers": {
            "<layerName>": {
              "isEnabled": true,
              "operationType": "<operation>",
              "executorType": "<optional: operation|emissionload|powershell|python|csharp>",
              "parameters": { ... },
              "dependsOn": ["<other layer names>"],
              "scriptPath": "<optional path to script>",
              "cloudAccountId": "<optional uuid — overrides the Essence-level CloudAccount for this layer; lets one Essence span multiple subscriptions or providers>",
              "_approach": "<optional description of what this layer does>"
            }
          }
        }

        Valid Azure operation types: CreateResourceGroup, DeleteResourceGroup, CreateVM, StartVM, StopVM, DeleteVM, CreateContainerGroup, StopContainerGroup, DeleteContainerGroup, CreateContainerRegistry, BuildContainerImage, DeployArmTemplate, HttpHealthCheck

        Common VM parameters: { "resourceGroup": "...", "vmName": "...", "location": "...", "vmSize": "Standard_B2s", "adminUsername": "...", "osDiskSizeGB": 30, "imagePublisher": "Canonical", "imageOffer": "0001-com-ubuntu-server-jammy", "imageSku": "22_04-lts-gen2" }

        Common ACI parameters: { "resourceGroup": "...", "containerGroupName": "...", "location": "...", "containers": [{ "name": "...", "image": "...", "cpu": 1, "memoryInGB": 1.5, "ports": [{ "port": 80, "protocol": "TCP" }] }] }

        Rules:
        - Output ONLY valid JSON, no markdown fences, no explanation
        - Layers must have unique names (use descriptive names like "create-rg", "deploy-vm", "health-check")
        - Set dependsOn correctly so layers execute in the right order
        - Always include isEnabled: true on each layer
        - For VM deployments, include a CreateResourceGroup layer first
        - Keep the essence focused and practical
        """;

    // Placeholder until Epic 1 lands the AWS handler set. Operation list is the
    // intended target surface; the model can use it to scaffold reasonable JSON
    // even before the handlers exist.
    private const string AwsSystemPrompt = """
        You are an infrastructure-as-code expert for the Aura Platform. Your job is to generate a valid Aura Essence JSON definition for AWS based on the user's natural language description.

        An Essence JSON has this structure:
        {
          "baseEssence": {
            "cloudProvider": "Aws",
            "defaultRegion": "<aws region, e.g. us-east-1>",
            "baseLoad": "<execution strategy, e.g. EmissionLoadEc2, EmissionLoadFargate>",
            "uniqueId": "<unique identifier>",
            "subscriptionId": "<aws account id>"
          },
          "layers": {
            "<layerName>": {
              "isEnabled": true,
              "operationType": "<operation>",
              "executorType": "<optional: operation|emissionload|powershell|python|csharp>",
              "parameters": { ... },
              "dependsOn": ["<other layer names>"],
              "scriptPath": "<optional path to script>",
              "cloudAccountId": "<optional uuid — overrides the Essence-level CloudAccount for this layer; lets one Essence span multiple subscriptions or providers>",
              "_approach": "<optional description of what this layer does>"
            }
          }
        }

        Target AWS operation types (handlers land in Epic 1): CreateVpc, DeleteVpc, CreateEc2Instance, StartEc2Instance, StopEc2Instance, TerminateEc2Instance, CreateS3Bucket, DeleteS3Bucket, RunEcsTask, DeployCloudFormation, CreateIamRole, HttpHealthCheck

        Common EC2 parameters: { "instanceType": "t3.small", "ami": "ami-...", "subnetId": "subnet-...", "keyName": "...", "securityGroupIds": ["sg-..."] }

        Common S3 parameters: { "bucketName": "...", "region": "us-east-1", "versioning": true, "publicAccessBlock": true }

        Rules:
        - Output ONLY valid JSON, no markdown fences, no explanation
        - Layers must have unique names (use descriptive names like "create-vpc", "launch-ec2", "health-check")
        - Set dependsOn correctly so layers execute in the right order
        - Always include isEnabled: true on each layer
        - For EC2 deployments, include a CreateVpc layer (or reference an existing VPC) first
        - Keep the essence focused and practical
        """;

    // Placeholder until Epic 2 lands the GCP handler set.
    private const string GcpSystemPrompt = """
        You are an infrastructure-as-code expert for the Aura Platform. Your job is to generate a valid Aura Essence JSON definition for Google Cloud based on the user's natural language description.

        An Essence JSON has this structure:
        {
          "baseEssence": {
            "cloudProvider": "Gcp",
            "defaultRegion": "<gcp region, e.g. us-east1>",
            "baseLoad": "<execution strategy, e.g. EmissionLoadGce, EmissionLoadCloudRun>",
            "uniqueId": "<unique identifier>",
            "subscriptionId": "<gcp project id>"
          },
          "layers": {
            "<layerName>": {
              "isEnabled": true,
              "operationType": "<operation>",
              "executorType": "<optional: operation|emissionload|powershell|python|csharp>",
              "parameters": { ... },
              "dependsOn": ["<other layer names>"],
              "scriptPath": "<optional path to script>",
              "cloudAccountId": "<optional uuid — overrides the Essence-level CloudAccount for this layer; lets one Essence span multiple subscriptions or providers>",
              "_approach": "<optional description of what this layer does>"
            }
          }
        }

        Target GCP operation types (handlers land in Epic 2): CreateNetwork, DeleteNetwork, CreateGceInstance, StartGceInstance, StopGceInstance, DeleteGceInstance, CreateGcsBucket, DeleteGcsBucket, DeployCloudRunService, DeployDeploymentManager, CreateServiceAccount, CreateFirewallRule, HttpHealthCheck

        Common GCE parameters: { "machineType": "e2-small", "zone": "us-east1-b", "sourceImage": "projects/debian-cloud/global/images/family/debian-12", "network": "default", "tags": [...] }

        Common GCS parameters: { "bucketName": "...", "location": "us-east1", "storageClass": "STANDARD", "uniformBucketLevelAccess": true }

        Rules:
        - Output ONLY valid JSON, no markdown fences, no explanation
        - Layers must have unique names (use descriptive names like "create-network", "launch-gce", "health-check")
        - Set dependsOn correctly so layers execute in the right order
        - Always include isEnabled: true on each layer
        - For GCE deployments, include a CreateNetwork layer (or reference an existing network) first
        - Keep the essence focused and practical
        """;

    private static string GetSystemPromptForProvider(CloudProvider provider) => provider switch
    {
        CloudProvider.Aws => AwsSystemPrompt,
        CloudProvider.Gcp => GcpSystemPrompt,
        _                 => AzureSystemPrompt, // Azure (and any unknown) — preserves prior behavior
    };

    public AiEssenceBuilderService(
        ILlmProviderFactory providerFactory, UserAiKeyService keyService,
        AuraDbContext db, ILogger<AiEssenceBuilderService> logger)
    {
        _providerFactory = providerFactory;
        _keyService = keyService;
        _db = db;
        _logger = logger;
    }

    public async Task<GenerateEssenceResponse> GenerateAsync(
        Guid userId, GenerateEssenceRequest request, Guid tenantId, CancellationToken ct)
    {
        var provider = _providerFactory.GetProvider(request.Provider);

        var apiKey = await _keyService.GetDecryptedKeyAsync(userId, request.Provider, ct)
            ?? throw new InvalidOperationException(
                $"No API key configured for provider '{request.Provider}'. Add one in Account Settings.");

        // Look up the target cloud to pick the right system prompt. Default to
        // Azure if the account isn't found — keeps legacy callers working.
        var cloudProvider = await _db.CloudAccounts
            .Where(c => c.Id == request.CloudAccountId)
            .Select(c => (CloudProvider?)c.Provider)
            .FirstOrDefaultAsync(ct) ?? CloudProvider.Azure;
        var systemPrompt = GetSystemPromptForProvider(cloudProvider);

        var sw = Stopwatch.StartNew();
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        var model = request.Model ?? "";
        string? essenceJson = null;
        var iterations = 0;
        string? lastError = null;
        string? providerError = null;

        for (var i = 0; i < MaxIterations; i++)
        {
            iterations++;
            var userPrompt = i == 0
                ? request.Prompt
                : $"The previous response was not a valid Aura Essence. Error: {lastError}\n\nPlease fix and return ONLY valid Aura Essence JSON.\n\nOriginal request: {request.Prompt}";

            var llmRequest = new LlmRequest(systemPrompt, userPrompt, apiKey, request.Model);
            var result = await provider.GenerateAsync(llmRequest, ct);

            totalInputTokens += result.InputTokens;
            totalOutputTokens += result.OutputTokens;
            if (!string.IsNullOrEmpty(result.Model))
                model = result.Model;

            // Timeouts, HTTP errors and bad keys are not retried: the provider already
            // failed the request, and re-sending it would only repeat the same failure.
            if (!result.Success)
            {
                providerError = result.Error;
                break;
            }

            // Strip markdown fences if present, then hold the draft to the same checks
            // the worker applies at deployment time.
            var candidate = ExtractJson(result.Content);
            lastError = ValidateEssence(candidate);
            if (lastError is null)
            {
                essenceJson = candidate;
                break;
            }
        }

        sw.Stop();

        // Log usage for BOTH outcomes — tokens burned in failed iterations are paid
        // usage and must be recorded, not lost when we throw (#21).
        var log = new AiGenerationLog
        {
            TenantId = tenantId,
            UserId = userId,
            ProviderName = request.Provider,
            Model = model,
            Prompt = request.Prompt,
            InputTokens = totalInputTokens,
            OutputTokens = totalOutputTokens,
            Iterations = iterations,
            DurationMs = sw.ElapsedMilliseconds,
            Success = essenceJson is not null
        };
        _db.Set<AiGenerationLog>().Add(log);
        await _db.SaveChangesAsync(ct);

        if (essenceJson is null)
        {
            var reason = providerError is not null
                ? $"LLM provider error: {providerError}"
                : $"Failed to generate valid essence JSON after {iterations} attempts. Last error: {lastError}";

            _logger.LogWarning(
                "AI essence generation failed: provider={Provider}, model={Model}, tokens={In}+{Out}, iterations={Iter}, duration={Ms}ms, reason={Reason}",
                request.Provider, model, totalInputTokens, totalOutputTokens, iterations, sw.ElapsedMilliseconds, reason);
            throw new InvalidOperationException(reason);
        }

        _logger.LogInformation(
            "AI essence generated: provider={Provider}, model={Model}, tokens={In}+{Out}, iterations={Iter}, duration={Ms}ms",
            request.Provider, model, totalInputTokens, totalOutputTokens, iterations, sw.ElapsedMilliseconds);

        return new GenerateEssenceResponse(
            essenceJson, totalInputTokens, totalOutputTokens,
            iterations, sw.ElapsedMilliseconds, model);
    }

    // Returns null when the draft is a runnable essence, otherwise the reason it is not.
    // Uses the worker's own parser so "valid here" means "will parse at deployment".
    private static string? ValidateEssence(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("layers", out var layers)
                || layers.ValueKind != JsonValueKind.Object)
                return "JSON is valid but missing an object-valued 'layers' property";

            return DeploymentOrchestrationService.ParseAndSortLayers(json, Guid.Empty).Count == 0
                ? "essence defines no enabled layers"
                : null;
        }
        catch (JsonException ex)
        {
            return $"response is not valid JSON: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"essence failed validation: {ex.Message}";
        }
    }

    private static string ExtractJson(string content)
    {
        content = content.Trim();

        // Strip markdown code fences
        if (content.StartsWith("```"))
        {
            var firstNewline = content.IndexOf('\n');
            if (firstNewline > 0)
                content = content[(firstNewline + 1)..];
            if (content.EndsWith("```"))
                content = content[..^3];
            content = content.Trim();
        }

        return content;
    }
}
