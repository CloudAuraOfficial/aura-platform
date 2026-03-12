using Aura.Core.Enums;
using Xunit;

namespace Aura.Tests;

public class RbacTests
{
    [Fact]
    public void UserRole_HasThreeValues()
    {
        var roles = Enum.GetValues<UserRole>();
        Assert.Equal(3, roles.Length);
        Assert.Contains(UserRole.Admin, roles);
        Assert.Contains(UserRole.Member, roles);
        Assert.Contains(UserRole.Operator, roles);
    }

    [Fact]
    public void UserRole_Admin_IsZero()
    {
        Assert.Equal(0, (int)UserRole.Admin);
    }

    [Fact]
    public void UserRole_Operator_IsTwo()
    {
        Assert.Equal(2, (int)UserRole.Operator);
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Member", true)]
    [InlineData("Operator", true)]
    public void AllRoles_CanBeParsedFromString(string roleStr, bool expected)
    {
        var result = Enum.TryParse<UserRole>(roleStr, out _);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Admin,Member", "Admin", true)]
    [InlineData("Admin,Member", "Member", true)]
    [InlineData("Admin,Member", "Operator", false)]
    [InlineData("Admin,Member,Operator", "Operator", true)]
    public void RoleAuthorization_ChecksContainment(string allowedRoles, string userRole, bool expected)
    {
        var allowed = allowedRoles.Split(',');
        Assert.Equal(expected, allowed.Contains(userRole));
    }
}
