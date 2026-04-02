using System.ComponentModel.DataAnnotations;

namespace Aura.Core.DTOs;

public sealed record GenerateEssenceRequest(
    [Required][MaxLength(2000)] string Prompt,
    [Required] Guid CloudAccountId,
    [Required][MaxLength(50)] string Provider,
    [MaxLength(100)] string? Model = null
);

public sealed record GenerateEssenceResponse(
    string EssenceJson,
    int InputTokens,
    int OutputTokens,
    int Iterations,
    long DurationMs,
    string Model
);
