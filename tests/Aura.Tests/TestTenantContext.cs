using Aura.Core.Interfaces;

namespace Aura.Tests;

internal sealed record FakeTenant(Guid TenantId) : ITenantContext;
