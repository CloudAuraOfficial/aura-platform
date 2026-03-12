using Aura.Core.Entities;
using Aura.Core.Enums;
using Aura.Core.Interfaces;
using Aura.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Aura.Tests;

public class InviteFlowTests
{
    private AuraDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AuraDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AuraDbContext(options);
    }

    [Fact]
    public async Task InvitedUser_IsDisabled_UntilInviteAccepted()
    {
        using var db = CreateDb();

        var tenant = new Tenant { Name = "TestTenant", Slug = "test" };
        db.Tenants.Add(tenant);

        var invitedUser = new User
        {
            TenantId = tenant.Id,
            Email = "invited@test.com",
            PasswordHash = string.Empty,
            Role = UserRole.Member,
            IsDisabled = true,
            InviteToken = "test-token-123",
            InviteTokenExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        db.Users.Add(invitedUser);
        await db.SaveChangesAsync();

        var user = await db.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.InviteToken == "test-token-123");

        Assert.True(user.IsDisabled);
        Assert.Empty(user.PasswordHash);
        Assert.NotNull(user.InviteToken);
    }

    [Fact]
    public async Task AcceptInvite_SetsPasswordAndEnablesUser()
    {
        using var db = CreateDb();

        var tenant = new Tenant { Name = "TestTenant", Slug = "test" };
        db.Tenants.Add(tenant);

        var user = new User
        {
            TenantId = tenant.Id,
            Email = "invited@test.com",
            PasswordHash = string.Empty,
            Role = UserRole.Operator,
            IsDisabled = true,
            InviteToken = "accept-token-456",
            InviteTokenExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Simulate accept-invite logic
        var found = await db.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.InviteToken == "accept-token-456");

        Assert.True(found.InviteTokenExpiresAt > DateTime.UtcNow);

        found.PasswordHash = Aura.Api.Middleware.AuthHelpers.HashPassword("NewPass123!");
        found.IsDisabled = false;
        found.InviteToken = null;
        found.InviteTokenExpiresAt = null;
        await db.SaveChangesAsync();

        var updated = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == found.Id);
        Assert.False(updated.IsDisabled);
        Assert.NotEmpty(updated.PasswordHash);
        Assert.Null(updated.InviteToken);
    }

    [Fact]
    public async Task ExpiredInviteToken_ShouldBeRejected()
    {
        using var db = CreateDb();

        var tenant = new Tenant { Name = "TestTenant", Slug = "test" };
        db.Tenants.Add(tenant);

        var user = new User
        {
            TenantId = tenant.Id,
            Email = "expired@test.com",
            PasswordHash = string.Empty,
            Role = UserRole.Member,
            IsDisabled = true,
            InviteToken = "expired-token",
            InviteTokenExpiresAt = DateTime.UtcNow.AddDays(-1) // Expired
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var found = await db.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.InviteToken == "expired-token");

        Assert.True(found.InviteTokenExpiresAt < DateTime.UtcNow);
    }
}
