using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aura.Core.Interfaces;

namespace Aura.Infrastructure.Services;

// Single implementation for every OpenAI-compatible /chat/completions API.
// Concrete providers (openai, openrouter, future gateways) are DI registrations
// in Program.cs, not classes. Keys are supplied per-request (request.ApiKey).
//
// Contract: GenerateAsync never throws for provider-side problems — bad key
// format, transport failure, timeout, non-JSON body, 200-with-error-body
// (OpenRouter documents these for moderation/upstream failures), missing
// choices, or malformed usage all return a failed LlmCompletionResult.
// Only caller-initiated cancellation propagates.
public class OpenAiCompatibleLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly string _defaultModel;

    public string ProviderName { get; }

    public OpenAiCompatibleLlmProvider(HttpClient http, string providerName, string apiUrl, string defaultModel)
    {
        _http = http;
        ProviderName = providerName;
        _apiUrl = apiUrl;
        _defaultModel = defaultModel;
    }

    public async Task<LlmCompletionResult> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = request.Model ?? _defaultModel;
        LlmCompletionResult Fail(string error) => new("", 0, 0, model, false, error);

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
        try
        {
            // Trim: keys pasted/stored with a trailing newline are common and
            // make AuthenticationHeaderValue throw FormatException.
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
        }
        catch (FormatException)
        {
            return Fail($"{ProviderName}: stored API key contains characters invalid in an Authorization header — re-enter the key");
        }
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

        string body;
        try
        {
            using var response = await _http.SendAsync(httpRequest, ct);
            body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return Fail($"{ProviderName} API error {(int)response.StatusCode}: {TruncateError(body)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Fail($"{ProviderName} request failed: {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return Fail($"{ProviderName} returned no choices: {TruncateError(body)}");
            }

            var content = choices[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrEmpty(content))
            {
                // Refusal / length-truncated completions come back as null
                // content on some upstreams; succeeding with "" burns the
                // caller's paid retry loop on unparseable output.
                return Fail($"{ProviderName} returned an empty completion: {TruncateError(body)}");
            }

            var inputTokens = 0;
            var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    inputTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var cot) && cot.ValueKind == JsonValueKind.Number)
                    outputTokens = cot.GetInt32();
            }

            return new LlmCompletionResult(content, inputTokens, outputTokens, model, true);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Fail($"{ProviderName} returned an unparseable response: {TruncateError(body)}");
        }
    }

    private static string TruncateError(string body) =>
        body.Length > 500 ? body[..500] : body;
}
