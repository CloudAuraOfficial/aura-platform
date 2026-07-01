using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

// OpenRouter is an OpenAI-compatible /chat/completions gateway, so this is a
// near-verbatim copy of OpenAiLlmProvider — the only structural differences are
// the provider name, the (env-configurable) base URL, and a namespaced default
// model. Keys are supplied per-request (request.ApiKey), so no secret is baked in.
public class OpenRouterLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private const string DefaultModel = "openai/gpt-4o"; // OpenRouter ids are namespaced vendor/model
    private const string DefaultApiUrl = "https://openrouter.ai/api/v1/chat/completions";

    public string ProviderName => "openrouter";

    public OpenRouterLlmProvider(HttpClient http)
    {
        _http = http;
        _apiUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ?? DefaultApiUrl;
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
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return new LlmCompletionResult("", 0, 0, model, false,
                $"OpenRouter API error {(int)response.StatusCode}: {TruncateError(body)}");
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
