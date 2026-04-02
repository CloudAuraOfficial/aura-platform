using System.Text;
using System.Text.Json;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

public class AnthropicLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private const string DefaultModel = "claude-sonnet-4-20250514";
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";

    public string ProviderName => "anthropic";

    public AnthropicLlmProvider(HttpClient http)
    {
        _http = http;
    }

    public async Task<LlmCompletionResult> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = request.Model ?? DefaultModel;

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
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        httpRequest.Headers.Add("x-api-key", request.ApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new LlmCompletionResult("", 0, 0, model, false,
                $"Anthropic API error {(int)response.StatusCode}: {TruncateError(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var content = "";
        if (root.TryGetProperty("content", out var contentArr) && contentArr.GetArrayLength() > 0)
        {
            content = contentArr[0].GetProperty("text").GetString() ?? "";
        }

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = usage.GetProperty("input_tokens").GetInt32();
            outputTokens = usage.GetProperty("output_tokens").GetInt32();
        }

        return new LlmCompletionResult(content, inputTokens, outputTokens, model, true);
    }

    private static string TruncateError(string body) =>
        body.Length > 500 ? body[..500] : body;
}
