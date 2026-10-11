using Aura.Api.Middleware;
using Aura.Core.DTOs;
using Aura.Core.Entities;
using Aura.Core.Exceptions;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aura.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Member")]
[Route("api/v1/cloud-accounts")]
public class CloudAccountsController : ControllerBase
{
    private readonly AuraDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICryptoService _crypto;
    private readonly ICloudCredentialTester _tester;
    private readonly ILogger<CloudAccountsController> _logger;

    public CloudAccountsController(
        AuraDbContext db,
        ITenantContext tenant,
        ICryptoService crypto,
        ICloudCredentialTester tester,
        ILogger<CloudAccountsController> logger)
    {
        _db = db;
        _tenant = tenant;
        _crypto = crypto;
        _tester = tester;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        (offset, limit) = PaginationDefaults.Clamp(offset, limit);
        var query = _db.CloudAccounts.OrderBy(c => c.CreatedAt);
        var total = await query.CountAsync();
        var items = await query.Skip(offset).Take(limit)
            .Select(c => ToDto(c)).ToListAsync();

        return Ok(new PaginatedResponse<CloudAccountResponse>(items, total, offset, limit));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var account = await _db.CloudAccounts.FindAsync(id);
        if (account is null)
            return NotFound(new ErrorResponse("not_found", "Cloud account not found.", 404));

        return Ok(ToDto(account));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCloudAccountRequest request)
    {
        var account = new CloudAccount
        {
            TenantId = _tenant.TenantId,
            Provider = request.Provider,
            Label = request.Label,
            EncryptedCredentials = _crypto.Encrypt(request.Credentials)
        };

        _db.CloudAccounts.Add(account);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = account.Id }, ToDto(account));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCloudAccountRequest request)
    {
        var account = await _db.CloudAccounts.FindAsync(id);
        if (account is null)
            return NotFound(new ErrorResponse("not_found", "Cloud account not found.", 404));

        if (request.Label is not null)
            account.Label = request.Label;

        if (request.Credentials is not null)
            account.EncryptedCredentials = _crypto.Encrypt(request.Credentials);

        await _db.SaveChangesAsync();
        return Ok(ToDto(account));
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var account = await _db.CloudAccounts.FindAsync(new object[] { id }, ct);
        if (account is null)
            return NotFound(new ErrorResponse("not_found", "Cloud account not found.", 404));

        var credsJson = DecryptCredentials(account);
        await UpgradeLegacyCredentialsAsync(account, credsJson, ct);
        var result = await _tester.TestAsync(account.Provider, credsJson, ct);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var account = await _db.CloudAccounts.FindAsync(id);
        if (account is null)
            return NotFound(new ErrorResponse("not_found", "Cloud account not found.", 404));

        _db.CloudAccounts.Remove(account);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // Names the account so the middleware can return an actionable 422. The middleware maps the exception.
    private string DecryptCredentials(CloudAccount account)
    {
        try
        {
            return _crypto.Decrypt(account.EncryptedCredentials);
        }
        catch (StoredSecretUnreadableException ex)
        {
            throw StoredSecretUnreadableException.ForCloudAccount(account.Label, ex);
        }
    }

    // Re-encrypts a legacy row after a successful read by its owner. Best effort: a failed save must
    // not fail the test call, because the legacy value still decrypts. Only the error type is logged.
    private async Task UpgradeLegacyCredentialsAsync(CloudAccount account, string credsJson, CancellationToken ct)
    {
        if (!_crypto.IsLegacyFormat(account.EncryptedCredentials))
            return;

        try
        {
            account.EncryptedCredentials = _crypto.Encrypt(credsJson);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Re-encrypted legacy cloud account credentials: account={AccountId}", account.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Could not re-encrypt legacy cloud account credentials: account={AccountId}, error={ErrorType}",
                account.Id, ex.GetType().Name);
        }
    }

    private static CloudAccountResponse ToDto(CloudAccount c) =>
        new(c.Id, c.Provider.ToString(), c.Label, c.CreatedAt);
}
