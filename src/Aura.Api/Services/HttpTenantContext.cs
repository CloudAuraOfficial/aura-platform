using Aura.Core.Interfaces;

namespace Aura.Api.Services;

public class HttpTenantContext : ITenantContext
{
    public Guid TenantId { get; }

    public HttpTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        var claim = httpContextAccessor.HttpContext?.User.FindFirst("tenant_id");
        TenantId = claim is not null ? Guid.Parse(claim.Value) : Guid.Empty;
    }
}
