using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Member,Operator")]
[Route("api/v1/account")]
public class AccountSettingsController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICryptoService _crypto;

    private static readonly HashSet<string> SupportedProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai", "anthropic"
    };

    public AccountSettingsController(AuraDbContext db, ITenantContext tenant, ICryptoService crypto)
    {
        _db = db;
        _tenant = tenant;
        _crypto = crypto;
    }

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)
            ?? User.FindFirst("sub");
        return sub is not null ? Guid.Parse(sub.Value) : Guid.Empty;
    }

    [HttpGet("ai-providers")]
    public async Task<IActionResult> List()
    {
        var userId = GetCurrentUserId();
        var providers = await _db.UserAiProviders
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.ProviderName)
            .ToListAsync();

        return Ok(providers.Select(p => new AiProviderResponse(
            p.Id, p.ProviderName, p.DisplayLabel,
            !string.IsNullOrEmpty(p.EncryptedApiKey), p.UpdatedAt)));
    }

    [HttpPost("ai-providers")]
    public async Task<IActionResult> Create([FromBody] CreateAiProviderRequest request)
    {
        var providerName = request.ProviderName.ToLowerInvariant();
        if (!SupportedProviders.Contains(providerName))
            return BadRequest(new ErrorResponse("bad_request",
                $"Unsupported provider. Supported: {string.Join(", ", SupportedProviders)}", 400));

        var userId = GetCurrentUserId();
        var existing = await _db.UserAiProviders
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ProviderName == providerName);

        if (existing is not null)
            return Conflict(new ErrorResponse("conflict",
                $"Provider '{providerName}' already configured. Use PUT to update.", 409));

        var provider = new UserAiProvider
        {
            TenantId = _tenant.TenantId,
            UserId = userId,
            ProviderName = providerName,
            EncryptedApiKey = _crypto.Encrypt(request.ApiKey),
            DisplayLabel = request.DisplayLabel
        };

        _db.UserAiProviders.Add(provider);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(List), null,
            new AiProviderResponse(provider.Id, provider.ProviderName,
                provider.DisplayLabel, true, provider.UpdatedAt));
    }

    [HttpPut("ai-providers/{providerName}")]
    public async Task<IActionResult> Update(string providerName, [FromBody] UpdateAiProviderRequest request)
    {
        providerName = providerName.ToLowerInvariant();
        var userId = GetCurrentUserId();
        var provider = await _db.UserAiProviders
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ProviderName == providerName);

        if (provider is null)
            return NotFound(new ErrorResponse("not_found", "Provider not configured.", 404));

        provider.EncryptedApiKey = _crypto.Encrypt(request.ApiKey);
        if (request.DisplayLabel is not null)
            provider.DisplayLabel = request.DisplayLabel;
        provider.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new AiProviderResponse(provider.Id, provider.ProviderName,
            provider.DisplayLabel, true, provider.UpdatedAt));
    }

    [HttpDelete("ai-providers/{providerName}")]
    public async Task<IActionResult> Delete(string providerName)
    {
        providerName = providerName.ToLowerInvariant();
        var userId = GetCurrentUserId();
        var provider = await _db.UserAiProviders
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ProviderName == providerName);

        if (provider is null)
            return NotFound(new ErrorResponse("not_found", "Provider not configured.", 404));

        _db.UserAiProviders.Remove(provider);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("supported-providers")]
    public IActionResult GetSupportedProviders()
    {
        var providers = new[]
        {
            new { name = "openai", label = "OpenAI", models = new[] { "gpt-4o", "gpt-4o-mini" } },
            new { name = "anthropic", label = "Anthropic", models = new[] { "claude-sonnet-4-20250514", "claude-haiku-4-5-20251001" } }
        };
        return Ok(providers);
    }
}
