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
[Authorize]
[Route("api/v1/users")]
public class UsersController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;

    public UsersController(AuraDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
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

        return CreatedAtAction(nameof(Get), new { id = user.Id }, ToDto(user));
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

        if (request.Role.HasValue)
            user.Role = request.Role.Value;

        await _db.SaveChangesAsync();
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
        return NoContent();
    }

    private static UserResponse ToDto(User u) =>
        new(u.Id, u.Email, u.Role.ToString(), u.CreatedAt);
}
