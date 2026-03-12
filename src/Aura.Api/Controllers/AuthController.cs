using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly string _jwtSecret;
    private readonly string _jwtIssuer;
    private readonly string _jwtAudience;
    private readonly int _jwtExpiryMinutes;

    public AuthController(AuraDbContext db)
    {
        _db = db;
        _jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")!;
        _jwtIssuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "aura-platform";
        _jwtAudience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "aura-platform";
        _jwtExpiryMinutes = int.TryParse(
            Environment.GetEnvironmentVariable("JWT_EXPIRY_MINUTES"), out var m) ? m : 60;
    }

    [HttpPost("bootstrap")]
    public async Task<IActionResult> Bootstrap([FromBody] BootstrapRequest request)
    {
        var anyTenant = await _db.Tenants.IgnoreQueryFilters().AnyAsync();
        if (anyTenant)
            return Conflict(new ErrorResponse("conflict", "Platform already bootstrapped.", 409));

        var tenant = new Tenant
        {
            Name = request.TenantName,
            Slug = request.TenantName.ToLowerInvariant().Replace(" ", "-")
        };
        _db.Tenants.Add(tenant);

        var user = new User
        {
            TenantId = tenant.Id,
            Email = request.Email,
            PasswordHash = AuthHelpers.HashPassword(request.Password),
            Role = UserRole.Admin
        };
        _db.Users.Add(user);

        await _db.SaveChangesAsync();

        var token = AuthHelpers.GenerateJwt(
            user.Id, tenant.Id, user.Email, user.Role.ToString(),
            _jwtSecret, _jwtIssuer, _jwtAudience, _jwtExpiryMinutes);

        return Ok(new LoginResponse(token, DateTime.UtcNow.AddMinutes(_jwtExpiryMinutes)));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == request.Email);

        if (user is null || !AuthHelpers.VerifyPassword(request.Password, user.PasswordHash))
            return Unauthorized(new ErrorResponse("unauthorized", "Invalid email or password.", 401));

        var token = AuthHelpers.GenerateJwt(
            user.Id, user.TenantId, user.Email, user.Role.ToString(),
            _jwtSecret, _jwtIssuer, _jwtAudience, _jwtExpiryMinutes);

        return Ok(new LoginResponse(token, DateTime.UtcNow.AddMinutes(_jwtExpiryMinutes)));
    }
}
