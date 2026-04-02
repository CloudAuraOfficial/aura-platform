using System.Diagnostics;
using System.Text.Json;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Aura.Infrastructure.Services;

namespace Aura.Api.Services;

public class AiEssenceBuilderService
{
    private readonly ILlmProviderFactory _providerFactory;
    private readonly UserAiKeyService _keyService;
    private readonly AuraDbContext _db;
    private readonly ILogger<AiEssenceBuilderService> _logger;

    private const int MaxIterations = 3;

    private const string SystemPrompt = """
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

        var sw = Stopwatch.StartNew();
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        var model = request.Model ?? "";
        string? essenceJson = null;
        var iterations = 0;
        string? lastError = null;

        for (var i = 0; i < MaxIterations; i++)
        {
            iterations++;
            var userPrompt = i == 0
                ? request.Prompt
                : $"The previous response was not valid JSON. Error: {lastError}\n\nPlease fix and return ONLY valid Aura Essence JSON.\n\nOriginal request: {request.Prompt}";

            var llmRequest = new LlmRequest(SystemPrompt, userPrompt, apiKey, request.Model);
            var result = await provider.GenerateAsync(llmRequest, ct);

            totalInputTokens += result.InputTokens;
            totalOutputTokens += result.OutputTokens;
            if (!string.IsNullOrEmpty(result.Model))
                model = result.Model;

            if (!result.Success)
                throw new InvalidOperationException($"LLM provider error: {result.Error}");

            // Try to extract JSON from the response (strip markdown fences if present)
            var content = ExtractJson(result.Content);

            // Validate JSON
            try
            {
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.TryGetProperty("layers", out _))
                {
                    essenceJson = content;
                    break;
                }
                lastError = "JSON is valid but missing 'layers' property";
            }
            catch (JsonException ex)
            {
                lastError = ex.Message;
            }
        }

        sw.Stop();

        if (essenceJson is null)
            throw new InvalidOperationException(
                $"Failed to generate valid essence JSON after {iterations} attempts. Last error: {lastError}");

        // Log usage
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
            Success = true
        };
        _db.Set<AiGenerationLog>().Add(log);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AI essence generated: provider={Provider}, model={Model}, tokens={In}+{Out}, iterations={Iter}, duration={Ms}ms",
            request.Provider, model, totalInputTokens, totalOutputTokens, iterations, sw.ElapsedMilliseconds);

        return new GenerateEssenceResponse(
            essenceJson, totalInputTokens, totalOutputTokens,
            iterations, sw.ElapsedMilliseconds, model);
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
