using QafOnPrem.Api.Contracts;

namespace QafOnPrem.Api.Services.AppData;

public enum SaveUserOutcome
{
    Saved = 0,
    NotFound = 1,
    DuplicateEmail = 2,
    InvalidRole = 3,
    UserLimitReached = 4,
}

public enum UserDeletionOutcome
{
    Deleted = 0,
    NotFound = 1,
    Blocked = 2,
}

public sealed class SaveUserResult
{
    public SaveUserOutcome Outcome { get; init; }

    public UserDetailDto? User { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class DeleteUserResult
{
    public UserDeletionOutcome Outcome { get; init; }

    public string? ErrorMessage { get; init; }
}

public static class UserRules
{
    public static bool CanAddActiveUser(int currentActiveUserCount, long? maxUsers)
    {
        return !maxUsers.HasValue || maxUsers.Value <= 0 || currentActiveUserCount < maxUsers.Value;
    }

    public static string FormatDeleteBlockedMessage(long? userId, IReadOnlyList<string> reasons, bool bulkDelete)
    {
        var joinedReasons = string.Join(", ", reasons);
        return bulkDelete
            ? $"User {userId} cannot be deleted because it is linked to: {joinedReasons}"
            : $"User cannot be deleted because it is linked to: {joinedReasons}";
    }
}