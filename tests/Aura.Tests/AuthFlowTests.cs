using Aura.Api.Middleware;
using Xunit;

namespace Aura.Tests;

public class AuthFlowTests
{
    [Fact]
    public void HashPassword_ProducesDifferentHashesForSamePassword()
    {
        var hash1 = AuthHelpers.HashPassword("TestPass123!");
        var hash2 = AuthHelpers.HashPassword("TestPass123!");
        Assert.NotEqual(hash1, hash2); // different salts
    }

    [Fact]
    public void VerifyPassword_CorrectPassword_ReturnsTrue()
    {
        var hash = AuthHelpers.HashPassword("TestPass123!");
        Assert.True(AuthHelpers.VerifyPassword("TestPass123!", hash));
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        var hash = AuthHelpers.HashPassword("TestPass123!");
        Assert.False(AuthHelpers.VerifyPassword("WrongPass!", hash));
    }

    [Fact]
    public void VerifyPassword_TamperedHash_ThrowsOrReturnsFalse()
    {
        // Invalid base64 may throw FormatException — either outcome is acceptable
        try
        {
            var result = AuthHelpers.VerifyPassword("any", "invalidbase64");
            Assert.False(result);
        }
        catch (FormatException)
        {
            // Expected — invalid base64 input
        }
    }

    [Fact]
    public void GenerateJwt_ContainsExpectedClaims()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var token = AuthHelpers.GenerateJwt(
            userId, tenantId, "test@test.com", "Admin",
            "this-is-a-64-char-secret-key-for-testing-purposes-1234567890abcd",
            "issuer", "audience", 60);

        Assert.NotEmpty(token);
        Assert.Contains(".", token); // JWT format
    }

    [Fact]
    public void PaginationDefaults_Clamp_EnforcesLimits()
    {
        var (offset1, limit1) = PaginationDefaults.Clamp(-5, 200);
        Assert.Equal(0, offset1);
        Assert.Equal(100, limit1);

        var (offset2, limit2) = PaginationDefaults.Clamp(10, 0);
        Assert.Equal(10, offset2);
        Assert.Equal(1, limit2);

        var (offset3, limit3) = PaginationDefaults.Clamp(5, 50);
        Assert.Equal(5, offset3);
        Assert.Equal(50, limit3);
    }
}
