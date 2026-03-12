namespace Aura.Core.DTOs;

public sealed record ErrorResponse(string Error, string Message, int StatusCode);
