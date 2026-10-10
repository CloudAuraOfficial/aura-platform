namespace Aura.Api.Services;

// Turns the LLM base-URL settings into the endpoint each provider POSTs to. A blank setting
// counts as unset and falls back to the public default: an empty variable must never become
// a relative URL, which HttpClient rejects with an exception the providers do not handle.
public static class LlmEndpointUrls
{
    public static string ChatCompletions(string? configured, string defaultBaseUrl) =>
        Resolve(configured, defaultBaseUrl, "/chat/completions");

    public static string AnthropicMessages(string? configured, string defaultBaseUrl) =>
        Resolve(configured, defaultBaseUrl, "/messages");

    private static string Resolve(string? configured, string defaultBaseUrl, string endpointPath)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? defaultBaseUrl : configured.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"LLM base URL '{baseUrl}' must be an absolute http or https URL.");
        }

        // Accepts a path-less base (OpenAI SDK convention) or the full endpoint.
        return baseUrl.EndsWith(endpointPath, StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl.TrimEnd('/') + endpointPath;
    }
}
