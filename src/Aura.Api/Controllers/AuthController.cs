using System.Security.Cryptography;
using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
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
    private readonly IAuditService _audit;
    private readonly string _jwtSecret;
    private readonly string _jwtIssuer;
    private readonly string _jwtAudience;
    private readonly int _jwtExpiryMinutes;

    private const int MaxFailedAttempts = 5;
    private const int LockoutMinutes = 15;
    private const int RefreshTokenDays = 7;

    public AuthController(AuraDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
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

        var passwordError = ValidatePasswordComplexity(request.Password);
        if (passwordError is not null)
            return BadRequest(new ErrorResponse("bad_request", passwordError, 400));

        var tenant = new Tenant
        {
            Name = request.TenantName,
            Slug = request.TenantName.ToLowerInvariant().Replace(" ", "-")
        };
        _db.Tenants.Add(tenant);

        var refreshToken = GenerateRefreshToken();
        var user = new User
        {
            TenantId = tenant.Id,
            Email = request.Email,
            PasswordHash = AuthHelpers.HashPassword(request.Password),
            Role = UserRole.Admin,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenDays)
        };
        _db.Users.Add(user);

        await _db.SaveChangesAsync();

        var token = AuthHelpers.GenerateJwt(
            user.Id, tenant.Id, user.Email, user.Role.ToString(),
            _jwtSecret, _jwtIssuer, _jwtAudience, _jwtExpiryMinutes);

        return Ok(new LoginResponse(token, refreshToken, DateTime.UtcNow.AddMinutes(_jwtExpiryMinutes)));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == request.Email);

        if (user is null)
            return Unauthorized(new ErrorResponse("unauthorized", "Invalid email or password.", 401));

        if (user.IsDisabled)
            return Unauthorized(new ErrorResponse("unauthorized", "Account is disabled.", 401));

        if (user.LockedUntil.HasValue && user.LockedUntil > DateTime.UtcNow)
        {
            var remaining = (int)(user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes + 1;
            return Unauthorized(new ErrorResponse("locked",
                $"Account locked. Try again in {remaining} minute(s).", 401));
        }

        if (!AuthHelpers.VerifyPassword(request.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= MaxFailedAttempts)
            {
                user.LockedUntil = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                user.FailedLoginAttempts = 0;
            }
            await _db.SaveChangesAsync();
            return Unauthorized(new ErrorResponse("unauthorized", "Invalid email or password.", 401));
        }

        // Successful login — reset lockout state
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        user.RefreshToken = GenerateRefreshToken();
        user.RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenDays);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(user.TenantId, user.Id, "login", "User", user.Id);

        var token = AuthHelpers.GenerateJwt(
            user.Id, user.TenantId, user.Email, user.Role.ToString(),
            _jwtSecret, _jwtIssuer, _jwtAudience, _jwtExpiryMinutes);

        return Ok(new LoginResponse(token, user.RefreshToken, DateTime.UtcNow.AddMinutes(_jwtExpiryMinutes)));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.RefreshToken == request.RefreshToken);

        if (user is null || user.IsDisabled)
            return Unauthorized(new ErrorResponse("unauthorized", "Invalid refresh token.", 401));

        if (user.RefreshTokenExpiresAt < DateTime.UtcNow)
            return Unauthorized(new ErrorResponse("unauthorized", "Refresh token expired.", 401));

        // Rotate refresh token
        user.RefreshToken = GenerateRefreshToken();
        user.RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenDays);
        await _db.SaveChangesAsync();

        var token = AuthHelpers.GenerateJwt(
            user.Id, user.TenantId, user.Email, user.Role.ToString(),
            _jwtSecret, _jwtIssuer, _jwtAudience, _jwtExpiryMinutes);

        return Ok(new LoginResponse(token, user.RefreshToken, DateTime.UtcNow.AddMinutes(_jwtExpiryMinutes)));
    }

    [HttpPost("accept-invite")]
    public async Task<IActionResult> AcceptInvite([FromBody] AcceptInviteRequest request)
    {
        var user = await _db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.InviteToken == request.InviteToken);

        if (user is null)
            return BadRequest(new ErrorResponse("bad_request", "Invalid invite token.", 400));

        if (user.InviteTokenExpiresAt < DateTime.UtcNow)
            return BadRequest(new ErrorResponse("bad_request", "Invite token has expired.", 400));

        var passwordError = ValidatePasswordComplexity(request.Password);
        if (passwordError is not null)
            return BadRequest(new ErrorResponse("bad_request", passwordError, 400));

        user.PasswordHash = AuthHelpers.HashPassword(request.Password);
        user.IsDisabled = false;
        user.InviteToken = null;
        user.InviteTokenExpiresAt = null;
        user.RefreshToken = GenerateRefreshToken();
        user.RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenDays);

        await _db.SaveChangesAsync();

        await _audit.LogAsync(user.TenantId, user.Id, "accept-invite", "User", user.Id);

        var token = AuthHelpers.GenerateJwt(
            user.Id, user.TenantId, user.Email, user.Role.ToString(),
            _jwtSecret, _jwtIssuer, _jwtAudience, _jwtExpiryMinutes);

        return Ok(new LoginResponse(token, user.RefreshToken!, DateTime.UtcNow.AddMinutes(_jwtExpiryMinutes)));
    }

    internal static string? ValidatePasswordComplexity(string password)
    {
        if (password.Length < 8)
            return "Password must be at least 8 characters.";
        if (!password.Any(char.IsUpper))
            return "Password must contain at least one uppercase letter.";
        if (!password.Any(char.IsLower))
            return "Password must contain at least one lowercase letter.";
        if (!password.Any(char.IsDigit))
            return "Password must contain at least one digit.";
        if (!password.Any(c => !char.IsLetterOrDigit(c)))
            return "Password must contain at least one special character.";
        return null;
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }
}
