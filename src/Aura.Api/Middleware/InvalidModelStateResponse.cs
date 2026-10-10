using Aura.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Aura.Api.Middleware;

// Replaces ASP.NET's default ValidationProblemDetails for [ApiController] 400s so
// request-validation failures use the same {error, message, statusCode} shape as
// every other API error. The dashboard reads `message`; without this it sees nothing.
public static class InvalidModelStateResponse
{
    public static IActionResult Create(ActionContext context)
    {
        var messages = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .SelectMany(e => e.Value!.Errors.Select(err => string.IsNullOrWhiteSpace(err.ErrorMessage)
                ? $"Invalid value for {e.Key}."
                : err.ErrorMessage));

        return new BadRequestObjectResult(
            new ErrorResponse("bad_request", string.Join(" ", messages), 400));
    }
}
