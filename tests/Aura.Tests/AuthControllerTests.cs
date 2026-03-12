using Aura.Api.Middleware;
using Xunit;

namespace Aura.Tests;

public class AuthControllerTests
{
    [Fact]
    public void ValidatePasswordComplexity_ValidPassword_ReturnsNull()
    {
        Assert.Null(Aura.Api.Controllers.AuthController.ValidatePasswordComplexity("MyP@ss1234"));
    }

    [Fact]
    public void ValidatePasswordComplexity_TooShort_ReturnsError()
    {
        var result = Aura.Api.Controllers.AuthController.ValidatePasswordComplexity("Aa1!");
        Assert.NotNull(result);
        Assert.Contains("8 characters", result);
    }

    [Fact]
    public void ValidatePasswordComplexity_NoUppercase_ReturnsError()
    {
        var result = Aura.Api.Controllers.AuthController.ValidatePasswordComplexity("mypass1!");
        Assert.NotNull(result);
        Assert.Contains("uppercase", result);
    }

    [Fact]
    public void ValidatePasswordComplexity_NoLowercase_ReturnsError()
    {
        var result = Aura.Api.Controllers.AuthController.ValidatePasswordComplexity("MYPASS1!");
        Assert.NotNull(result);
        Assert.Contains("lowercase", result);
    }

    [Fact]
    public void ValidatePasswordComplexity_NoDigit_ReturnsError()
    {
        var result = Aura.Api.Controllers.AuthController.ValidatePasswordComplexity("MyPass!!");
        Assert.NotNull(result);
        Assert.Contains("digit", result);
    }

    [Fact]
    public void ValidatePasswordComplexity_NoSpecial_ReturnsError()
    {
        var result = Aura.Api.Controllers.AuthController.ValidatePasswordComplexity("MyPass123");
        Assert.NotNull(result);
        Assert.Contains("special", result);
    }

    [Fact]
    public void HashPassword_And_VerifyPassword_RoundTrip()
    {
        var password = "TestPass123!";
        var hash = AuthHelpers.HashPassword(password);
        Assert.True(AuthHelpers.VerifyPassword(password, hash));
    }

    [Fact]
    public void HashPassword_DifferentSalts_ProduceDifferentHashes()
    {
        var password = "TestPass123!";
        var h1 = AuthHelpers.HashPassword(password);
        var h2 = AuthHelpers.HashPassword(password);
        Assert.NotEqual(h1, h2);
        // But both verify correctly
        Assert.True(AuthHelpers.VerifyPassword(password, h1));
        Assert.True(AuthHelpers.VerifyPassword(password, h2));
    }

    [Fact]
    public void GenerateJwt_ReturnsValidFormat()
    {
        var token = AuthHelpers.GenerateJwt(
            Guid.NewGuid(), Guid.NewGuid(), "test@test.com", "Admin",
            "this-is-a-64-char-secret-key-for-testing-purposes-1234567890abcd",
            "issuer", "audience", 60);

        Assert.NotEmpty(token);
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length); // header.payload.signature
    }

    [Fact]
    public void GenerateJwt_DifferentUsers_ProduceDifferentTokens()
    {
        var secret = "this-is-a-64-char-secret-key-for-testing-purposes-1234567890abcd";
        var t1 = AuthHelpers.GenerateJwt(Guid.NewGuid(), Guid.NewGuid(), "a@a.com", "Admin", secret, "iss", "aud", 60);
        var t2 = AuthHelpers.GenerateJwt(Guid.NewGuid(), Guid.NewGuid(), "b@b.com", "Member", secret, "iss", "aud", 60);
        Assert.NotEqual(t1, t2);
    }
}
