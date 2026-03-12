using System.ComponentModel.DataAnnotations;
using Aura.Core.Enums;

namespace Aura.Core.DTOs;

public sealed record CreateCloudAccountRequest(
    [Required] CloudProvider Provider,
    [Required, StringLength(200, MinimumLength = 1)] string Label,
    [Required, StringLength(10000, MinimumLength = 1)] string Credentials
);

public sealed record UpdateCloudAccountRequest(
    [StringLength(200)] string? Label,
    [StringLength(10000)] string? Credentials
);

public sealed record CloudAccountResponse(
    Guid Id,
    string Provider,
    string Label,
    DateTime CreatedAt
);
