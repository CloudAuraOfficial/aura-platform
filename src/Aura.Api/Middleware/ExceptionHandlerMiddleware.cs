using System.Text.Json;
using Aura.Core.DTOs;
using Aura.Core.Exceptions;

namespace Aura.Api.Middleware;

public class ExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlerMiddleware> _logger;

    public ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (StoredSecretUnreadableException ex)
        {
            // A user-state problem, not a server fault: the user has to re-enter the secret. The message is
            // written for the user and never contains the secret. Callers log it where the secret is identified.
            context.Response.StatusCode = 422;
            context.Response.ContentType = "application/json";

            var error = new ErrorResponse("stored_secret_unreadable", ex.Message, 422);
            await context.Response.WriteAsJsonAsync(error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";

            var error = new ErrorResponse("internal_error", "An unexpected error occurred.", 500);
            await context.Response.WriteAsJsonAsync(error);
        }
    }
}
