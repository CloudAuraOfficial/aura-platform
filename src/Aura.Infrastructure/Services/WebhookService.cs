using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aura.Infrastructure.Services;

public class WebhookService
{
    private readonly HttpClient _http;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(HttpClient http, ILogger<WebhookService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task NotifyAsync(string webhookUrl, Guid runId, string status, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            runId,
            status,
            timestamp = DateTime.UtcNow
        });

        try
        {
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(webhookUrl, content, ct);
            _logger.LogInformation("Webhook {Url} responded {StatusCode} for run {RunId}",
                webhookUrl, (int)response.StatusCode, runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook {Url} failed for run {RunId}", webhookUrl, runId);
        }
    }
}
