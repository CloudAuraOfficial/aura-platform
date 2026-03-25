using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Aura.Worker.Executors;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Azure;

public class ImportContainerImageHandler : IOperationHandler
{
    private readonly ILogger<ImportContainerImageHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public ImportContainerImageHandler(
        ILogger<ImportContainerImageHandler> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("sourceImage", out var sourceImageProp))
            return new LayerExecutionResult(false, "Missing required parameter: sourceImage");

        if (!parameters.TryGetProperty("targetImage", out var targetImageProp))
            return new LayerExecutionResult(false, "Missing required parameter: targetImage");

        if (!parameters.TryGetProperty("registryName", out var registryNameProp))
            return new LayerExecutionResult(false, "Missing required parameter: registryName");

        if (!parameters.TryGetProperty("resourceGroup", out var rgProp))
            return new LayerExecutionResult(false, "Missing required parameter: resourceGroup");

        var sourceImage = sourceImageProp.GetString()!;
        var targetImage = targetImageProp.GetString()!;
        var registryName = registryNameProp.GetString()!;
        var resourceGroup = rgProp.GetString()!;

        try
        {
            // Parse source image into registryUri + sourceImage
            // e.g. "docker.io/library/postgres:16-alpine" → registryUri="docker.io", sourceRef="library/postgres:16-alpine"
            var registryUri = "docker.io";
            var sourceRef = sourceImage;

            if (sourceImage.Contains('/'))
            {
                var firstSlash = sourceImage.IndexOf('/');
                var possibleRegistry = sourceImage[..firstSlash];
                if (possibleRegistry.Contains('.'))
                {
                    registryUri = possibleRegistry;
                    sourceRef = sourceImage[(firstSlash + 1)..];
                }
            }

            // Get subscription ID from env vars
            var subscriptionId = envVars.GetValueOrDefault("AZURE_SUBSCRIPTION_ID", "");
            if (string.IsNullOrEmpty(subscriptionId))
            {
                // Try to get from ArmClient
                var armClient = AzureClientFactory.Create(envVars);
                var sub = await armClient.GetDefaultSubscriptionAsync(ct);
                subscriptionId = sub.Data.SubscriptionId;
            }

            // Get access token
            var tenantId = envVars.GetValueOrDefault("AZURE_TENANT_ID", "");
            var clientId = envVars.GetValueOrDefault("AZURE_CLIENT_ID", "");
            var clientSecret = envVars.GetValueOrDefault("AZURE_CLIENT_SECRET", "");

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var token = await credential.GetTokenAsync(
                new global::Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }), ct);

            // Build the exact request body that az acr import uses
            var requestBody = JsonSerializer.Serialize(new
            {
                source = new
                {
                    registryUri = registryUri,
                    sourceImage = sourceRef
                },
                targetTags = new[] { targetImage },
                mode = "Force"
            });

            var apiUrl = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                         $"/resourceGroups/{resourceGroup}" +
                         $"/providers/Microsoft.ContainerRegistry/registries/{registryName}" +
                         $"/importImage?api-version=2023-07-01";

            _logger.LogInformation(
                "Importing image {RegistryUri}/{SourceRef} as {Target} into {Registry}",
                registryUri, sourceRef, targetImage, registryName);

            var client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var response = await client.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted ||
                response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                // Poll the operation until complete if there's a Location header
                if (response.Headers.Location != null)
                {
                    var pollUrl = response.Headers.Location.ToString();
                    var maxPoll = 120;
                    var elapsed = 0;

                    while (elapsed < maxPoll)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        elapsed += 5;

                        var pollReq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                        pollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                        var pollResp = await client.SendAsync(pollReq, ct);

                        if (pollResp.StatusCode == System.Net.HttpStatusCode.OK)
                        {
                            _logger.LogInformation("Image import completed for {Target}", targetImage);
                            break;
                        }

                        if (pollResp.StatusCode != System.Net.HttpStatusCode.Accepted)
                        {
                            var errorBody = await pollResp.Content.ReadAsStringAsync(ct);
                            return new LayerExecutionResult(false,
                                $"Import polling failed: HTTP {(int)pollResp.StatusCode}. {errorBody}");
                        }

                        _logger.LogInformation("Import in progress... ({Elapsed}s)", elapsed);
                    }
                }

                return new LayerExecutionResult(true,
                    $"Image '{registryUri}/{sourceRef}' imported as '{targetImage}' into registry '{registryName}'.");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return new LayerExecutionResult(false,
                $"Import failed: HTTP {(int)response.StatusCode}. {body}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import container image into {Registry}", registryName);
            return new LayerExecutionResult(false, $"Failed to import container image: {ex.Message}");
        }
    }
}
