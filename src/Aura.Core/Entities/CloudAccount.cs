using Aura.Core.Enums;

namespace Aura.Core.Entities;

public class CloudAccount : TenantScopedEntity
{
    public CloudProvider Provider { get; set; }
    public string Label { get; set; } = string.Empty;
    public string EncryptedCredentials { get; set; } = string.Empty;
}
