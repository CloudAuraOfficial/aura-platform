using Aura.Core.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Aura.Api.Middleware;

// Request-validation failures (from [ApiController] model binding) use the same
// {error, message, statusCode} shape as every other API error, instead of the
// framework's ProblemDetails. Clients can then rely on one error contract.
public static class InvalidModelStateResponse
{
    public static IActionResult Create(ActionContext context)
    {
        var message = context.ModelState.Values
            .SelectMany(entry => entry.Errors)
            .Select(error => error.ErrorMessage)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?? "Request is invalid.";

        return new BadRequestObjectResult(new ErrorResponse("bad_request", message, 400));
    }
}
