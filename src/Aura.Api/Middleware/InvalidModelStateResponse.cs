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
        // ModelState is a dictionary, so pick the error by a fixed order rather than whatever
        // comes first: field-specific errors before body-level ones, then by field name.
        var errors = context.ModelState
            .Where(entry => entry.Value is not null)
            .SelectMany(entry => entry.Value!.Errors.Select(error => (Field: entry.Key, Error: error)))
            .OrderBy(item => string.IsNullOrEmpty(item.Field) ? 1 : 0)
            .ThenBy(item => item.Field, StringComparer.Ordinal)
            .ThenBy(item => item.Error.ErrorMessage, StringComparer.Ordinal)
            .ToList();

        var message = errors.Count > 0
            ? Describe(errors[0].Field, errors[0].Error)
            : "Request is invalid.";

        return new BadRequestObjectResult(new ErrorResponse("bad_request", message, 400));
    }

    // Validation attribute messages already name the field, so they pass through. Parser failures
    // carry .NET type names and byte offsets in their text: name the field and drop the detail.
    private static string Describe(string field, ModelError error)
    {
        if (IsParserText(error) || string.IsNullOrWhiteSpace(error.ErrorMessage))
        {
            return string.IsNullOrEmpty(field)
                ? "Request body is malformed."
                : $"'{field}' has an invalid value.";
        }

        return error.ErrorMessage;
    }

    // The JSON input formatter records conversion failures as text ("... | LineNumber: 0 |
    // BytePositionInLine: 45."), not as an exception, so both forms are checked.
    private static bool IsParserText(ModelError error) =>
        error.Exception is not null
        || error.ErrorMessage.Contains("BytePositionInLine", StringComparison.Ordinal)
        || error.ErrorMessage.Contains("JSON value", StringComparison.Ordinal);
}
