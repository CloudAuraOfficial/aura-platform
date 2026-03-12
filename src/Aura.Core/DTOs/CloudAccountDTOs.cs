using Aura.Core.Enums;

namespace Aura.Core.DTOs;

public sealed record CreateCloudAccountRequest(
    CloudProvider Provider,
    string Label,
    string Credentials
);

public sealed record UpdateCloudAccountRequest(string? Label, string? Credentials);

public sealed record CloudAccountResponse(
    Guid Id,
    string Provider,
    string Label,
    DateTime CreatedAt
);
