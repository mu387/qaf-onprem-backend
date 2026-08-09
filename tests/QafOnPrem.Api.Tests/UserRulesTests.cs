using QafOnPrem.Api.Services.AppData;
using Xunit;

namespace QafOnPrem.Api.Tests;

public sealed class UserRulesTests
{
    [Theory]
    [InlineData(1, 2L, true)]
    [InlineData(2, 2L, false)]
    [InlineData(2, null, true)]
    public void CanAddActiveUser_ReturnsExpectedResult(int currentActiveUserCount, long? maxUsers, bool expected)
    {
        var result = UserRules.CanAddActiveUser(currentActiveUserCount, maxUsers);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatDeleteBlockedMessage_UsesSingleDeleteMessage_WhenNotBulkDelete()
    {
        var message = UserRules.FormatDeleteBlockedMessage(null, ["projects", "defects"], bulkDelete: false);

        Assert.Equal("User cannot be deleted because it is linked to: projects, defects", message);
    }

    [Fact]
    public void FormatDeleteBlockedMessage_UsesBulkDeleteMessage_WhenBulkDelete()
    {
        var message = UserRules.FormatDeleteBlockedMessage(27, ["test plans (assigned)"], bulkDelete: true);

        Assert.Equal("User 27 cannot be deleted because it is linked to: test plans (assigned)", message);
    }
}