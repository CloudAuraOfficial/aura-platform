using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

public class OpenAiLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private const string DefaultModel = "gpt-4o";
    private const string ApiUrl = "https://api.openai.com/v1/chat/completions";

    public string ProviderName => "openai";

    public OpenAiLlmProvider(HttpClient http)
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
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new LlmCompletionResult("", 0, 0, model, false,
                $"OpenAI API error {(int)response.StatusCode}: {TruncateError(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var content = root.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";

        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputTokens = usage.GetProperty("prompt_tokens").GetInt32();
            outputTokens = usage.GetProperty("completion_tokens").GetInt32();
        }

        return new LlmCompletionResult(content, inputTokens, outputTokens, model, true);
    }

    private static string TruncateError(string body) =>
        body.Length > 500 ? body[..500] : body;
}
