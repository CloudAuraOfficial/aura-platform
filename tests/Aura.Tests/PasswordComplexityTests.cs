using Aura.Api.Controllers;
using Xunit;

namespace Aura.Tests;

public class PasswordComplexityTests
{
    [Theory]
    [InlineData("Abcd1234!", null)]
    [InlineData("P@ssw0rd", null)]
    [InlineData("MyStr0ng!Pass", null)]
    public void ValidPasswords_ReturnNull(string password, string? expected)
    {
        Assert.Equal(expected, AuthController.ValidatePasswordComplexity(password));
    }

    [Fact]
    public void TooShort_ReturnsError()
    {
        var result = AuthController.ValidatePasswordComplexity("Ab1!");
        Assert.Contains("at least 8", result!);
    }

    [Fact]
    public void NoUppercase_ReturnsError()
    {
        var result = AuthController.ValidatePasswordComplexity("abcd1234!");
        Assert.Contains("uppercase", result!);
    }

    [Fact]
    public void NoLowercase_ReturnsError()
    {
        var result = AuthController.ValidatePasswordComplexity("ABCD1234!");
        Assert.Contains("lowercase", result!);
    }

    [Fact]
    public void NoDigit_ReturnsError()
    {
        var result = AuthController.ValidatePasswordComplexity("Abcdefgh!");
        Assert.Contains("digit", result!);
    }

    [Fact]
    public void NoSpecialChar_ReturnsError()
    {
        var result = AuthController.ValidatePasswordComplexity("Abcd1234");
        Assert.Contains("special", result!);
    }
}
