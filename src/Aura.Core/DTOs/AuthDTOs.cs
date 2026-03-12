namespace Aura.Core.DTOs;

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string Token, DateTime ExpiresAt);
public sealed record BootstrapRequest(string Email, string Password, string TenantName);
