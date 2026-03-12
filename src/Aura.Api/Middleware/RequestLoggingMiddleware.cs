using System.Diagnostics;

namespace Aura.Api.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N")[..12];
        context.Items["RequestId"] = requestId;

        try
        {
            await _next(context);
            sw.Stop();

            AuraMetrics.HttpRequestsTotal
                .WithLabels(context.Request.Method, context.Request.Path.Value ?? "/",
                    context.Response.StatusCode.ToString())
                .Inc();

            _logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms [rid:{RequestId}]",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds,
                requestId);
        }
        catch (Exception ex)
        {
            sw.Stop();

            _logger.LogError(ex,
                "HTTP {Method} {Path} threw {ExceptionType} in {ElapsedMs}ms [rid:{RequestId}]",
                context.Request.Method,
                context.Request.Path.Value,
                ex.GetType().Name,
                sw.ElapsedMilliseconds,
                requestId);

            throw;
        }
    }
}
