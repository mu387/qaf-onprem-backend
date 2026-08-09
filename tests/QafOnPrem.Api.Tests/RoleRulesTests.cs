using Xunit;

namespace QafOnPrem.Api.Tests;

public sealed class RoleRulesTests
{
    [Fact]
    public void NormalizeCreatedRoleName_PrefixesPlatformName_ForPlatformScope()
    {
        var result = QafOnPrem.Api.Services.AppData.RoleRules.NormalizeCreatedRoleName("Admin", isPlatformScope: true);

        Assert.Equal("Platform Admin", result);
    }

    [Fact]
    public void NormalizeCreatedRoleName_DoesNotDoublePrefix_WhenAlreadyPlatform()
    {
        var result = QafOnPrem.Api.Services.AppData.RoleRules.NormalizeCreatedRoleName("Platform Admin", isPlatformScope: true);

        Assert.Equal("Platform Admin", result);
    }

    [Theory]
    [InlineData(false, false, QafOnPrem.Api.Services.AppData.RoleDeletionOutcome.NotFound)]
    [InlineData(true, true, QafOnPrem.Api.Services.AppData.RoleDeletionOutcome.HasAssignedUsers)]
    [InlineData(true, false, QafOnPrem.Api.Services.AppData.RoleDeletionOutcome.Deleted)]
    public void EvaluateDeletion_ReturnsExpectedOutcome(bool roleExists, bool hasAssignedUsers, QafOnPrem.Api.Services.AppData.RoleDeletionOutcome expected)
    {
        var result = QafOnPrem.Api.Services.AppData.RoleRules.EvaluateDeletion(roleExists, hasAssignedUsers);

        Assert.Equal(expected, result);
    }
}