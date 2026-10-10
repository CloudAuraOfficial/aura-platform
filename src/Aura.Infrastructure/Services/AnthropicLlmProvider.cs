using System.Text;
using System.Text.Json;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

// Anthropic's /v1/messages API — not OpenAI-compatible, so it keeps its own
// class. Same contract as OpenAiCompatibleLlmProvider: GenerateAsync never
// throws for provider-side problems; only caller-initiated cancellation
// propagates.
public class AnthropicLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private const string DefaultModel = "claude-sonnet-4-20250514";
    private const string DefaultApiUrl = "https://api.anthropic.com/v1/messages";

    public string ProviderName => "anthropic";

    // apiUrl is the resolved messages endpoint (see LlmEndpointUrls, ANTHROPIC_BASE_URL) so
    // deployments can point at a proxy or gateway; the default is Anthropic's public endpoint.
    public AnthropicLlmProvider(HttpClient http, string apiUrl = DefaultApiUrl)
    {
        _http = http;
        _apiUrl = apiUrl;
    }

    public async Task<LlmCompletionResult> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = request.Model ?? DefaultModel;
        LlmCompletionResult Fail(string error, int? httpStatus = null) =>
            new("", 0, 0, model, false, error, httpStatus);

        var payload = new
        {
            model,
            max_tokens = request.MaxTokens,
            system = request.SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = request.UserPrompt }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
        try
        {
            httpRequest.Headers.Add("x-api-key", request.ApiKey.Trim());
        }
        catch (FormatException)
        {
            return Fail("anthropic: stored API key contains characters invalid in a header — re-enter the key");
        }
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        string body;
        try
        {
            using var response = await _http.SendAsync(httpRequest, ct);
            body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return Fail($"Anthropic API error {(int)response.StatusCode}: {TruncateError(body)}",
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Fail($"Anthropic request failed: {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("content", out var contentArr)
                || contentArr.ValueKind != JsonValueKind.Array
                || contentArr.GetArrayLength() == 0)
            {
                return Fail($"Anthropic returned no content: {TruncateError(body)}");
            }

            var content = contentArr[0].GetProperty("text").GetString();
            if (string.IsNullOrEmpty(content))
            {
                return Fail($"Anthropic returned an empty completion: {TruncateError(body)}");
            }

            var inputTokens = 0;
            var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number)
                    inputTokens = it.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number)
                    outputTokens = ot.GetInt32();
            }

            return new LlmCompletionResult(content, inputTokens, outputTokens, model, true);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Fail($"Anthropic returned an unparseable response: {TruncateError(body)}");
        }
    }

    private static string TruncateError(string body) =>
        body.Length > 500 ? body[..500] : body;
}
