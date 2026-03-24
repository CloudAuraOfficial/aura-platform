using System.Text.Json;
using Aura.Worker.Executors;
using Aura.Worker.Operations;
using Microsoft.Extensions.Logging;

namespace Aura.Worker.Operations.Common;

public class HttpHealthCheckHandler : IOperationHandler
{
    private readonly ILogger<HttpHealthCheckHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpHealthCheckHandler(
        ILogger<HttpHealthCheckHandler> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<LayerExecutionResult> ExecuteAsync(
        string layerName, JsonElement parameters, Dictionary<string, string> envVars,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetProperty("endpoint", out var endpointProp))
            return new LayerExecutionResult(false, "Missing required parameter: endpoint");

        var endpoint = endpointProp.GetString()!;

        var expectedStatus = 200;
        if (parameters.TryGetProperty("expectedStatus", out var statusProp))
            expectedStatus = statusProp.GetInt32();

        var maxRetries = 10;
        if (parameters.TryGetProperty("maxRetries", out var retriesProp))
            maxRetries = retriesProp.GetInt32();

        var retryDelaySeconds = 10;
        if (parameters.TryGetProperty("retryDelaySeconds", out var delayProp))
            retryDelaySeconds = delayProp.GetInt32();

        var client = _httpClientFactory.CreateClient();

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Health check attempt {Attempt}/{MaxRetries} for {Endpoint}",
                    attempt, maxRetries, endpoint);

                var response = await client.GetAsync(endpoint, ct);
                var statusCode = (int)response.StatusCode;

                if (statusCode == expectedStatus)
                {
                    return new LayerExecutionResult(true,
                        $"Health check passed on attempt {attempt}/{maxRetries}. " +
                        $"Status: {statusCode}. Endpoint: {endpoint}");
                }

                _logger.LogWarning(
                    "Health check returned {StatusCode}, expected {Expected}. Attempt {Attempt}/{MaxRetries}",
                    statusCode, expectedStatus, attempt, maxRetries);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Health check failed on attempt {Attempt}/{MaxRetries}: {Message}",
                    attempt, maxRetries, ex.Message);
            }

            if (attempt < maxRetries)
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), ct);
        }

        return new LayerExecutionResult(false,
            $"Health check failed after {maxRetries} attempts. Endpoint: {endpoint}");
    }
}
