using System.Diagnostics;
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
    private readonly IEssenceValidator _validator;
    private readonly ILogger<AiEssenceBuilderService> _logger;

    private const int MaxIterations = 3;

    internal const string AzureSystemPrompt = """
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
    internal const string AwsSystemPrompt = """
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
    internal const string GcpSystemPrompt = """
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

        Target GCP operation types (handlers land in Epic 2): CreateNetwork, DeleteNetwork, CreateGceInstance, StartGceInstance, StopGceInstance, DeleteGceInstance, CreateGcsBucket, DeleteGcsBucket, DeployCloudRunService, CreateServiceAccount, CreateFirewallRule, HttpHealthCheck

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
        AuraDbContext db, IEssenceValidator validator, ILogger<AiEssenceBuilderService> logger)
    {
        _providerFactory = providerFactory;
        _keyService = keyService;
        _db = db;
        _validator = validator;
        _logger = logger;
    }

    public async Task<GenerateEssenceResponse> GenerateAsync(
        Guid userId, GenerateEssenceRequest request, Guid tenantId, CancellationToken ct)
    {
        // [Required] on the request already rejects blank prompts over HTTP; this guard covers
        // direct callers of the service, where a blank prompt would still burn paid tokens.
        // No param name here: ArgumentException appends "(Parameter ...)" to Message,
        // which would leak into the client-facing error.
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt must not be blank.");

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
        int? providerStatus = null;

        try
        {
            for (var i = 0; i < MaxIterations; i++)
            {
                iterations++;
                var userPrompt = i == 0
                    ? request.Prompt
                    : $"The previous response was rejected. Error: {lastError}\n\nPlease fix and return ONLY valid Aura Essence JSON.\n\nOriginal request: {request.Prompt}";

                var llmRequest = new LlmRequest(systemPrompt, userPrompt, apiKey, request.Model);
                var result = await provider.GenerateAsync(llmRequest, ct);

                totalInputTokens += result.InputTokens;
                totalOutputTokens += result.OutputTokens;
                if (!string.IsNullOrEmpty(result.Model))
                    model = result.Model;

                // Provider failures (HTTP error, timeout) are not retried: break so the
                // usage recorded so far is still logged (#21).
                if (!result.Success)
                {
                    providerError = result.Error ?? "unknown provider error";
                    providerStatus = result.HttpStatus;
                    break;
                }

                // Try to extract JSON from the response (strip markdown fences if present)
                var content = ExtractJson(result.Content);

                var check = _validator.Validate(content, cloudProvider);
                lastError = check.Error;
                if (lastError is null)
                {
                    essenceJson = check.EssenceJson;
                    break;
                }
            }
        }
        finally
        {
            sw.Stop();

            // Usage is paid for however the attempts end: a normal result, a provider failure,
            // a client disconnect (ct is cancelled, so it must not reach SaveChanges), or any
            // unexpected exception. Hence the finally and CancellationToken.None.
            _db.Set<AiGenerationLog>().Add(new AiGenerationLog
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
            });
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        // Client-facing messages stay plain. Provider bodies and validator reasons go to the log.
        if (providerError is not null)
        {
            _logger.LogWarning(
                "AI essence generation failed at provider: provider={Provider}, iterations={Iter}, httpStatus={Status}, error={Error}",
                request.Provider, iterations, providerStatus, providerError);
            throw new InvalidOperationException(DescribeProviderFailure(providerStatus));
        }

        if (essenceJson is null)
        {
            _logger.LogWarning(
                "AI essence generation produced no valid essence: provider={Provider}, iterations={Iter}, lastError={Error}",
                request.Provider, iterations, lastError);
            throw new InvalidOperationException(
                $"The model returned an invalid essence after {iterations} attempts. Try rephrasing the prompt.");
        }

        _logger.LogInformation(
            "AI essence generated: provider={Provider}, model={Model}, tokens={In}+{Out}, iterations={Iter}, duration={Ms}ms",
            request.Provider, model, totalInputTokens, totalOutputTokens, iterations, sw.ElapsedMilliseconds);

        return new GenerateEssenceResponse(
            essenceJson, totalInputTokens, totalOutputTokens,
            iterations, sw.ElapsedMilliseconds, model);
    }

    // Client-facing text for a failed provider call. Status codes map to actionable wording;
    // the provider's own error body stays in the server log.
    internal static string DescribeProviderFailure(int? httpStatus) => httpStatus switch
    {
        401 or 403 => "The AI provider rejected the API key. Check the key in Account Settings.",
        null => "The AI provider request failed. Try again.",
        _ => $"The AI provider returned HTTP {httpStatus}. Try again later.",
    };

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
