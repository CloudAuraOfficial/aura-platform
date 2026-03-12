using System.Security.Cryptography;
using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/v1/users")]
public class UsersController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public UsersController(AuraDbContext db, ITenantContext tenant, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        (offset, limit) = PaginationDefaults.Clamp(offset, limit);
        var query = _db.Users.OrderBy(u => u.CreatedAt);
        var total = await query.CountAsync();
        var items = await query.Skip(offset).Take(limit)
            .Select(u => ToDto(u)).ToListAsync();

        return Ok(new PaginatedResponse<UserResponse>(items, total, offset, limit));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null)
            return NotFound(new ErrorResponse("not_found", "User not found.", 404));

        return Ok(ToDto(user));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        var passwordError = AuthController.ValidatePasswordComplexity(request.Password);
        if (passwordError is not null)
            return BadRequest(new ErrorResponse("bad_request", passwordError, 400));

        var exists = await _db.Users.AnyAsync(u => u.Email == request.Email);
        if (exists)
            return Conflict(new ErrorResponse("conflict", "Email already in use.", 409));

        var user = new User
        {
            TenantId = _tenant.TenantId,
            Email = request.Email,
            PasswordHash = AuthHelpers.HashPassword(request.Password),
            Role = request.Role
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var callerId = GetCurrentUserId();
        await _audit.LogAsync(_tenant.TenantId, callerId, "create", "User", user.Id, $"role={user.Role}");

        return CreatedAtAction(nameof(Get), new { id = user.Id }, ToDto(user));
    }

    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest request)
    {
        var exists = await _db.Users.AnyAsync(u => u.Email == request.Email);
        if (exists)
            return Conflict(new ErrorResponse("conflict", "Email already in use.", 409));

        var inviteToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var user = new User
        {
            TenantId = _tenant.TenantId,
            Email = request.Email,
            PasswordHash = string.Empty, // No password yet — must accept invite
            Role = request.Role,
            IsDisabled = true, // Disabled until invite is accepted
            InviteToken = inviteToken,
            InviteTokenExpiresAt = DateTime.UtcNow.AddDays(7)
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var callerId = GetCurrentUserId();
        await _audit.LogAsync(_tenant.TenantId, callerId, "invite", "User", user.Id, $"role={request.Role}");

        return Ok(new { user.Id, InviteToken = inviteToken, ExpiresAt = user.InviteTokenExpiresAt });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null)
            return NotFound(new ErrorResponse("not_found", "User not found.", 404));

        if (request.Email is not null)
        {
            var exists = await _db.Users.AnyAsync(u => u.Email == request.Email && u.Id != id);
            if (exists)
                return Conflict(new ErrorResponse("conflict", "Email already in use.", 409));
            user.Email = request.Email;
        }

        if (request.Password is not null)
        {
            var passwordError = AuthController.ValidatePasswordComplexity(request.Password);
            if (passwordError is not null)
                return BadRequest(new ErrorResponse("bad_request", passwordError, 400));

            user.PasswordHash = AuthHelpers.HashPassword(request.Password);
            // Revoke refresh token on password change
            user.RefreshToken = null;
            user.RefreshTokenExpiresAt = null;
        }

        if (request.Role.HasValue)
            user.Role = request.Role.Value;

        if (request.IsDisabled.HasValue)
        {
            user.IsDisabled = request.IsDisabled.Value;
            // Revoke refresh token when disabling a user
            if (request.IsDisabled.Value)
            {
                user.RefreshToken = null;
                user.RefreshTokenExpiresAt = null;
            }
        }

        await _db.SaveChangesAsync();

        var callerId = GetCurrentUserId();
        await _audit.LogAsync(_tenant.TenantId, callerId, "update", "User", user.Id);

        return Ok(ToDto(user));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null)
            return NotFound(new ErrorResponse("not_found", "User not found.", 404));

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();

        var callerId = GetCurrentUserId();
        await _audit.LogAsync(_tenant.TenantId, callerId, "delete", "User", id);

        return NoContent();
    }

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)
            ?? User.FindFirst("sub");
        return sub is not null ? Guid.Parse(sub.Value) : Guid.Empty;
    }

    private static UserResponse ToDto(User u) =>
        new(u.Id, u.Email, u.Role.ToString(), u.IsDisabled, u.CreatedAt);
}
